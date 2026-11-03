using System.Text;
using CommunityToolkit.WinUI.Controls;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace DeskBox.Views.SettingsSections;

public sealed partial class AiSettingsSection : UserControl
{
    private ISettingsWidgetActions? _widgetActions;
    private IntPtr _ownerWindow;
    private bool _isSectionLoaded;
    private bool _isDialogOpen;
    private bool _isAiMethodUiSyncing;
    private bool _pendingFocusAgentHub;
    private int _agentHubTabIndex;
    private McpConfigFormat _selectedMcpFormat = McpConfigFormat.StandardJson;
    private string? _selectedPlaybookWidgetId;
    private CancellationTokenSource? _inlineAiOpCts;
    private CancellationTokenSource? _bridgeSelfTestCts;

    public AiSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public event EventHandler<string>? NavigateToCustomWidgetRequested;

    private LocalizationService Localization => App.Current.LocalizationService;

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
        RebuildAgentHubCards();
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
        RebuildAgentHubCards();
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

    private void OnLanguageChanged()
    {
        if (!_isSectionLoaded)
        {
            return;
        }

        RefreshAiMethodSettingsUi();
        RebuildAgentHubCards();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (!_isSectionLoaded)
        {
            return;
        }

        RefreshAiMethodSettingsUi();
        RebuildAgentHubCards();
    }

    private void FocusAgentHubOnUi()
    {
        _pendingFocusAgentHub = false;
        DeveloperAgentExpander.IsExpanded = true;
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

    // ── Group 1 & 2: Direct API Model Service + Web AI Fallback ─────────────

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

        WebAiReadyBadgeText.Text = "✓ " + Localization.T("Settings.AiSettings.WebAiReadyBadge");
        DeveloperAgentExpander.Header = Localization.T("Settings.AiSettings.DevExpander.Header");
        DeveloperAgentExpander.Description = Localization.T("Settings.AiSettings.DevExpander.Description");
        DeveloperBridgeStatusText.Text = Localization.T("Settings.AiSettings.DevExpander.StatusReady");

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

        if (readyProvider is not null && !AiKeyExpander.IsExpanded)
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

        AiKeyTestHeaderButton.Visibility = selectedIsConfigured ? Visibility.Visible : Visibility.Collapsed;

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

        string keyLabel = Localization.T(selectedIsConfigured
            ? "CustomWidget.Settings.AiKey.EditButton"
            : (selectedPreset.RequiresApiKey
                ? "CustomWidget.Settings.AiKey.Button"
                : "CustomWidget.AiConfig.EnableLocalButton"));
        AiKeyActionButton.Content = keyLabel;
        AutomationProperties.SetName(AiKeyActionButton, keyLabel);
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
    }

    private async void AiInlineFetchModelsButton_Click(object sender, RoutedEventArgs e)
    {
        string providerId = ResolveSelectedAiProviderId();
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        AiProviderCredentialEntry? entry = CustomWidgetAiCredentialStore.Shared.Get(providerId);
        if (!DeskBoxAiService.IsProviderConfigured(preset, entry))
        {
            await ShowConfigureProviderKeyDialogAsync(providerId);
            if (_isSectionLoaded)
            {
                RefreshAiMethodSettingsUi();
            }
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
                if (AiKeyExpander.IsExpanded &&
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
            if (_isSectionLoaded)
            {
                AiInlineFetchModelsButton.IsEnabled = true;
            }
        }
    }

    private async void AiInlineTestButton_Click(object sender, RoutedEventArgs e)
    {
        SaveInlineModelChoice();
        string providerId = ResolveSelectedAiProviderId();
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        AiProviderCredentialEntry? entry = CustomWidgetAiCredentialStore.Shared.Get(providerId);
        if (!DeskBoxAiService.IsProviderConfigured(preset, entry))
        {
            await ShowConfigureProviderKeyDialogAsync(providerId);
            if (_isSectionLoaded)
            {
                RefreshAiMethodSettingsUi();
            }
            return;
        }

        _inlineAiOpCts?.Cancel();
        _inlineAiOpCts?.Dispose();
        var cts = new CancellationTokenSource();
        _inlineAiOpCts = cts;

        AiKeyTestHeaderButton.IsEnabled = false;
        AiInlineTestButton.IsEnabled = false;
        AiEndpointStatusCard.Description = Localization.T("CustomWidget.AiConfig.TestingInProgress");

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
                AiEndpointStatusCard.Description = "✓ " + msg;
                ShowBanner(InfoBarSeverity.Success, msg);
            }
            else
            {
                string err = res.ErrorMessage ?? Localization.T("CustomWidget.AiConfig.TestFailed");
                AiEndpointStatusCard.Description = err;
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
                AiEndpointStatusCard.Description = ex.Message;
                ShowBanner(InfoBarSeverity.Error, ex.Message);
            }
        }
        finally
        {
            if (_isSectionLoaded)
            {
                AiKeyTestHeaderButton.IsEnabled = true;
                AiInlineTestButton.IsEnabled = true;
            }
        }
    }

    private async void AiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string providerId = ResolveSelectedAiProviderId();
            string? savedProvider = await ShowConfigureProviderKeyDialogAsync(providerId);
            if (!string.IsNullOrWhiteSpace(savedProvider))
            {
                CustomWidgetStudioPreferencesStore.Shared.SetPreferredAiProviderId(savedProvider);
            }

            if (_isSectionLoaded)
            {
                RefreshAiMethodSettingsUi();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[AiSettings] Configure provider key dialog failed: {ex.Message}");
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
    }

    private async Task<string?> ShowConfigureProviderKeyDialogAsync(string providerId)
    {
        if (_isDialogOpen || XamlRoot is null)
        {
            return null;
        }

        _isDialogOpen = true;
        try
        {
            return await CustomWidgetAiConfigDialogHelper.ShowAsync(
                XamlRoot,
                ActualTheme,
                Localization,
                providerId);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    // ── Group 3: External Agent Ecosystem (MCP, Skills & HTTP Bridge) ───────

    private void RebuildAgentHubCards()
    {
        _bridgeSelfTestCts?.Cancel();
        _bridgeSelfTestCts?.Dispose();
        _bridgeSelfTestCts = new CancellationTokenSource();
        CancellationToken bridgeCt = _bridgeSelfTestCts.Token;

        AgentHubDynamicContentPanel.Children.Clear();

        // Zone 1: Live Bridge Diagnostics + Expandable 6 MCP Tools Manifest
        AgentHubDynamicContentPanel.Children.Add(BuildBridgeDiagnosticsBanner(bridgeCt));

        // Zone 2: 4-Pillar Universal Agent Segmented Workbench
        var tabBarRow = new Grid { Margin = new Thickness(0, 2, 0, 0), ColumnSpacing = 8 };
        tabBarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabBarRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hubSegmented = new Segmented
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(hubSegmented);
        hubSegmented.Loaded += (_, _) => WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(hubSegmented);
        hubSegmented.ActualThemeChanged += (_, _) => WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(hubSegmented);

        string[] tabLabels =
        [
            Localization.T("CustomWidget.AgentHub.Tab.Clients"),
            Localization.T("CustomWidget.AgentHub.Tab.UniversalMcp"),
            Localization.T("CustomWidget.AgentHub.Tab.SkillsWorkspace"),
            Localization.T("CustomWidget.AgentHub.Tab.HttpBridge")
        ];
        foreach (string label in tabLabels)
        {
            hubSegmented.Items.Add(new SegmentedItem
            {
                Padding = new Thickness(12, 5, 12, 5),
                Content = new TextBlock { Text = label }
            });
        }

        int clampedTab = Math.Clamp(_agentHubTabIndex, 0, tabLabels.Length - 1);
        hubSegmented.SelectedIndex = clampedTab;
        tabBarRow.Children.Add(hubSegmented);

        var helpAnchor = InfoTip.CreateAnchorButton(
            "CustomWidget.InfoTip.ExternalAi.Title",
            "CustomWidget.InfoTip.ExternalAi.Body");
        Grid.SetColumn(helpAnchor, 1);
        tabBarRow.Children.Add(helpAnchor);
        AgentHubDynamicContentPanel.Children.Add(tabBarRow);

        var tabContentHost = new StackPanel { Spacing = 8 };

        void RenderActiveTab(int index)
        {
            _agentHubTabIndex = Math.Clamp(index, 0, 3);
            tabContentHost.Children.Clear();
            switch (_agentHubTabIndex)
            {
                case 0:
                    BuildClientsTabContent(tabContentHost);
                    break;
                case 1:
                    BuildUniversalMcpTabContent(tabContentHost, () =>
                    {
                        hubSegmented.SelectedIndex = 3;
                    });
                    break;
                case 2:
                    BuildSkillsAndWorkspaceTabContent(tabContentHost);
                    break;
                case 3:
                    BuildHttpBridgeTabContent(tabContentHost);
                    break;
            }
        }

        hubSegmented.SelectionChanged += (_, _) =>
        {
            if (hubSegmented.SelectedIndex >= 0 && hubSegmented.SelectedIndex != _agentHubTabIndex)
            {
                RenderActiveTab(hubSegmented.SelectedIndex);
            }
        };

        RenderActiveTab(clampedTab);
        AgentHubDynamicContentPanel.Children.Add(tabContentHost);

        // Zone 3: Ready-to-Paste Playbooks + Ambient Agent HUD Quick-Add
        AgentHubDynamicContentPanel.Children.Add(BuildPlaybooksAndHudSection());
    }

    private FrameworkElement BuildBridgeDiagnosticsBanner(CancellationToken cancellationToken)
    {
        var outerStack = new StackPanel { Spacing = 6 };

        var statusCard = new Border
        {
            Style = (Style)Resources["SettingsGroupStyle"],
            Padding = new Thickness(16, 12, 16, 12)
        };

        var rowGrid = new Grid { ColumnSpacing = 12 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var statusDot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ResolveBrush("TextFillColorSecondaryBrush", Color.FromArgb(0x90, 0x80, 0x80, 0x80))
        };
        rowGrid.Children.Add(statusDot);

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleTb = new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.BridgeChecking"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        var subTb = new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.BridgeSubtitleChecking"),
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        textStack.Children.Add(titleTb);
        textStack.Children.Add(subTb);
        Grid.SetColumn(textStack, 1);
        rowGrid.Children.Add(textStack);

        var selfTestBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            Content = Localization.T("CustomWidget.AgentHub.SelfTestButton")
        };
        AutomationProperties.SetName(selfTestBtn, Localization.T("CustomWidget.AgentHub.SelfTestButton"));
        Grid.SetColumn(selfTestBtn, 2);
        rowGrid.Children.Add(selfTestBtn);

        statusCard.Child = rowGrid;
        outerStack.Children.Add(statusCard);

        IReadOnlyList<DeskBoxAgentCliEntry.McpToolSummary> tools = DeskBoxAgentCliEntry.GetMcpToolSummaries();
        var toolsExpander = new Expander
        {
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Header = new TextBlock
            {
                Text = Localization.Format("CustomWidget.AgentHub.McpToolsExpanderHeader", tools.Count),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            }
        };

        var toolsListStack = new StackPanel { Spacing = 8 };
        foreach (DeskBoxAgentCliEntry.McpToolSummary tool in tools)
        {
            var toolRow = new Grid { ColumnSpacing = 12 };
            toolRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            toolRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var codeBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Top,
                Background = ResolveBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
                Child = new TextBlock
                {
                    Text = tool.Name,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true
                }
            };
            toolRow.Children.Add(codeBadge);

            var descText = new TextBlock
            {
                Text = Localization.T(tool.DescriptionKey),
                Style = (Style)Resources["SettingDescriptionTextStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(descText, 1);
            toolRow.Children.Add(descText);
            toolsListStack.Children.Add(toolRow);
        }

        toolsExpander.Content = toolsListStack;
        outerStack.Children.Add(toolsExpander);

        async Task RunDiagnosticsAsync(bool showBannerFeedback)
        {
            selfTestBtn.IsEnabled = false;
            try
            {
                AgentBridgeSelfTestResult res = await CustomWidgetAgentConnectorService.RunBridgeSelfTestAsync(
                    cancellationToken: cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                bool healthy = res.McpProtocolOk && res.PipeOnline && (!res.HttpEnabled || res.HttpOnline);
                Brush indicatorBrush = healthy
                    ? ResolveBrush("SystemFillColorSuccessBrush", Color.FromArgb(0xFF, 0x10, 0x7C, 0x10))
                    : ResolveBrush("SystemFillColorCautionBrush", Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00));
                statusDot.Background = indicatorBrush;
                DeveloperBridgeStatusDot.Background = indicatorBrush;
                DeveloperBridgeStatusText.Text = healthy
                    ? Localization.T("Settings.AiSettings.DevExpander.StatusReady")
                    : Localization.T("CustomWidget.AgentHub.BridgeDegraded");

                if (healthy)
                {
                    titleTb.Text = res.HttpEnabled
                        ? Localization.Format("CustomWidget.AgentHub.BridgeReadyWithHttp", res.McpToolCount, res.HttpPort)
                        : Localization.Format("CustomWidget.AgentHub.BridgeReady", res.McpToolCount);
                    subTb.Text = Localization.Format(
                        "CustomWidget.AgentHub.BridgeSubtitleReady",
                        res.HostPid ?? 0,
                        res.ElapsedMs);
                }
                else
                {
                    titleTb.Text = Localization.T("CustomWidget.AgentHub.BridgeDegraded");
                    subTb.Text = !string.IsNullOrWhiteSpace(res.ErrorMessage)
                        ? res.ErrorMessage
                        : Localization.T("CustomWidget.AgentHub.BridgeSubtitleOffline");
                }

                if (showBannerFeedback)
                {
                    ShowBanner(
                        healthy ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                        healthy
                            ? Localization.Format("CustomWidget.AgentHub.SelfTestPassed", res.McpToolCount, res.ElapsedMs)
                            : (res.ErrorMessage ?? Localization.T("CustomWidget.AgentHub.BridgeSubtitleOffline")));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                statusDot.Background = ResolveBrush("SystemFillColorCriticalBrush", Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
                titleTb.Text = Localization.T("CustomWidget.AgentHub.BridgeDegraded");
                subTb.Text = ex.Message;
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    selfTestBtn.IsEnabled = true;
                }
            }
        }

        selfTestBtn.Click += async (_, _) => await RunDiagnosticsAsync(showBannerFeedback: true);
        _ = RunDiagnosticsAsync(showBannerFeedback: false);

        return outerStack;
    }

    private void BuildClientsTabContent(StackPanel container)
    {
        container.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.ClientsIntro"),
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            Margin = new Thickness(4, 0, 4, 2)
        });

        IReadOnlyList<ExternalAgentClientStatus> allClients = CustomWidgetAgentConnectorService
            .DetectAgentClients()
            .OrderBy(static c => c.IsConfigured ? 0 : c.IsInstalled ? 1 : 2)
            .ToList();

        List<ExternalAgentClientStatus> primaryClients = allClients
            .Where(static c => c.IsConfigured || c.IsInstalled)
            .ToList();
        List<ExternalAgentClientStatus> extraClients = allClients
            .Where(static c => !c.IsConfigured && !c.IsInstalled)
            .ToList();

        if (primaryClients.Count == 0)
        {
            primaryClients = allClients.ToList();
            extraClients.Clear();
        }

        container.Children.Add(CreateResponsiveClientGrid(primaryClients));

        if (extraClients.Count > 0)
        {
            var moreExpander = new Expander
            {
                IsExpanded = false,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Header = new TextBlock
                {
                    Text = Localization.Format("CustomWidget.AgentHub.MoreClientsExpander", extraClients.Count),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold
                },
                Content = CreateResponsiveClientGrid(extraClients)
            };
            container.Children.Add(moreExpander);
        }
    }

    private Grid CreateResponsiveClientGrid(IReadOnlyList<ExternalAgentClientStatus> clients)
    {
        var tilesGrid = new Grid
        {
            ColumnSpacing = 8,
            RowSpacing = 8
        };

        void LayoutClientTiles(double availableWidth)
        {
            int cols = availableWidth <= 0 || availableWidth >= 560 ? 2 : 1;
            tilesGrid.ColumnDefinitions.Clear();
            tilesGrid.RowDefinitions.Clear();
            for (int c = 0; c < cols; c++)
            {
                tilesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            int rows = (clients.Count + cols - 1) / cols;
            for (int r = 0; r < rows; r++)
            {
                tilesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (int i = 0; i < tilesGrid.Children.Count; i++)
            {
                if (tilesGrid.Children[i] is FrameworkElement child)
                {
                    Grid.SetRow(child, i / cols);
                    Grid.SetColumn(child, i % cols);
                }
            }
        }

        foreach (ExternalAgentClientStatus client in clients)
        {
            tilesGrid.Children.Add(BuildExternalAiAppTile(client));
        }

        LayoutClientTiles(ActualWidth);
        tilesGrid.SizeChanged += (_, e) => LayoutClientTiles(e.NewSize.Width);
        return tilesGrid;
    }

    private Border BuildExternalAiAppTile(ExternalAgentClientStatus client)
    {
        var tile = new Border
        {
            Style = (Style)Resources["SettingsGroupStyle"],
            Padding = new Thickness(16, 14, 16, 12),
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = !client.IsInstalled && !client.IsConfigured ? 0.88 : 1.0
        };

        if (!string.IsNullOrWhiteSpace(client.DetailPath))
        {
            ToolTipService.SetToolTip(tile, client.DetailPath);
        }

        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topRow = new Grid { ColumnSpacing = 10 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string iconGlyph = client.ClientId switch
        {
            CustomWidgetAgentConnectorService.ClientClaudeCode => "\uE756",
            CustomWidgetAgentConnectorService.ClientCodex => "\uE756",
            CustomWidgetAgentConnectorService.ClientDeepSeekHarness => "\uE756",
            CustomWidgetAgentConnectorService.ClientClaudeDesktop => "\uE8BD",
            CustomWidgetAgentConnectorService.ClientUniversalSkill => "\uE8F1",
            _ => "\uE943"
        };

        var iconBadge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(6),
            Background = ResolveBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x16, 0x80, 0x80, 0x80)),
            Child = new FontIcon
            {
                Glyph = iconGlyph,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        topRow.Children.Add(iconBadge);

        var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = client.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(client.TargetSummary))
        {
            titleStack.Children.Add(new TextBlock
            {
                Text = client.TargetSummary,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ResolveBrush("TextFillColorSecondaryBrush", Color.FromArgb(0xA8, 0x80, 0x80, 0x80))
            });
        }
        Grid.SetColumn(titleStack, 1);
        topRow.Children.Add(titleStack);

        string pillText;
        Brush pillFg;
        if (client.IsConfigured)
        {
            pillText = "● " + Localization.T("CustomWidget.AgentHub.PillConnected");
            pillFg = ResolveBrush("SystemFillColorSuccessBrush", Color.FromArgb(0xFF, 0x10, 0x7C, 0x10));
        }
        else if (client.IsInstalled)
        {
            pillText = Localization.T("CustomWidget.AgentHub.PillInstalled");
            pillFg = ResolveBrush("AccentTextFillColorPrimaryBrush", Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
        }
        else
        {
            pillText = Localization.T("CustomWidget.AgentHub.PillAvailable");
            pillFg = ResolveBrush("TextFillColorSecondaryBrush", Color.FromArgb(0xA8, 0x80, 0x80, 0x80));
        }

        var statusPill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ResolveBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            Child = new TextBlock
            {
                Text = pillText,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = pillFg
            }
        };
        Grid.SetColumn(statusPill, 2);
        topRow.Children.Add(statusPill);
        root.Children.Add(topRow);

        string friendlyBenefitKey = client.ClientId switch
        {
            CustomWidgetAgentConnectorService.ClientCursor => "CustomWidget.AgentHub.Benefit.Cursor",
            CustomWidgetAgentConnectorService.ClientClaudeCode => "CustomWidget.AgentHub.Benefit.ClaudeCode",
            CustomWidgetAgentConnectorService.ClientCodex => "CustomWidget.AgentHub.Benefit.Codex",
            CustomWidgetAgentConnectorService.ClientDeepSeekHarness => "CustomWidget.AgentHub.Benefit.DeepSeekHarness",
            CustomWidgetAgentConnectorService.ClientVsCode => "CustomWidget.AgentHub.Benefit.VsCode",
            CustomWidgetAgentConnectorService.ClientTrae => "CustomWidget.AgentHub.Benefit.Trae",
            CustomWidgetAgentConnectorService.ClientWindsurf => "CustomWidget.AgentHub.Benefit.Windsurf",
            CustomWidgetAgentConnectorService.ClientAntigravity => "CustomWidget.AgentHub.Benefit.Antigravity",
            CustomWidgetAgentConnectorService.ClientCline => "CustomWidget.AgentHub.Benefit.Cline",
            CustomWidgetAgentConnectorService.ClientClaudeDesktop => "CustomWidget.AgentHub.Benefit.ClaudeDesktop",
            CustomWidgetAgentConnectorService.ClientWorkBuddy => "CustomWidget.AgentHub.Benefit.WorkBuddy",
            _ => "CustomWidget.AgentHub.Benefit.Skill"
        };
        var descTb = new TextBlock
        {
            Text = Localization.T(friendlyBenefitKey),
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            LineHeight = 18,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(descTb, 1);
        root.Children.Add(descTb);

        var bottomBar = new Grid { ColumnSpacing = 8 };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (!string.IsNullOrWhiteSpace(client.DetailPath) &&
            (File.Exists(client.DetailPath) || Directory.Exists(Path.GetDirectoryName(client.DetailPath) ?? string.Empty)))
        {
            Button revealBtn = CreateSubtleStripButton(
                "\uE8DA",
                Localization.T("CustomWidget.AgentHub.RevealConfigShort"));
            ToolTipService.SetToolTip(revealBtn, client.DetailPath);
            revealBtn.Click += (_, _) => CustomWidgetAgentConnectorService.RevealPathInExplorer(client.DetailPath);
            bottomBar.Children.Add(revealBtn);
        }

        string btnLabel = client.IsConfigured
            ? Localization.T("CustomWidget.AgentHub.ResyncConfig")
            : Localization.T("CustomWidget.AgentHub.OneClickConnect");
        bool highlightPrimary = client.IsInstalled && !client.IsConfigured;

        var connectBtn = new Button
        {
            Height = 30,
            MinHeight = 30,
            MinWidth = 88,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = highlightPrimary
                ? (Style)Resources["AccentActionButtonStyle"]
                : (Style)Resources["ActionButtonStyle"],
            Content = btnLabel
        };
        connectBtn.Height = 30;
        connectBtn.MinHeight = 30;
        AutomationProperties.SetName(connectBtn, $"{client.DisplayName} - {btnLabel}");
        connectBtn.Click += async (_, _) =>
        {
            connectBtn.IsEnabled = false;
            try
            {
                (bool ok, string detail) = await CustomWidgetAgentConnectorService.ConnectClientAsync(client.ClientId);
                if (!_isSectionLoaded)
                {
                    return;
                }

                if (!ok)
                {
                    ShowBanner(InfoBarSeverity.Error, detail);
                    connectBtn.IsEnabled = true;
                    return;
                }

                RebuildAgentHubCards();
                ShowBanner(
                    InfoBarSeverity.Success,
                    Localization.Format("CustomWidget.AgentHub.ConnectedSuccessWithPath", client.DisplayName, detail));
            }
            catch (Exception ex)
            {
                if (_isSectionLoaded)
                {
                    ShowBanner(InfoBarSeverity.Error, ex.Message);
                    connectBtn.IsEnabled = true;
                }
            }
        };
        Grid.SetColumn(connectBtn, 1);
        bottomBar.Children.Add(connectBtn);

        Grid.SetRow(bottomBar, 2);
        root.Children.Add(bottomBar);

        tile.Child = root;
        return tile;
    }

    private void BuildUniversalMcpTabContent(StackPanel container, Action navigateToHttpTab)
    {
        var generatorCard = new Border
        {
            Style = (Style)Resources["SettingsGroupStyle"],
            Padding = new Thickness(16, 14, 16, 14)
        };
        var genStack = new StackPanel { Spacing = 10 };

        genStack.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.Universal.Title"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        genStack.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.Universal.Desc"),
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        });

        var controlsRow = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        controlsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controlsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var formatCombo = new ComboBox
        {
            MinWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(formatCombo, Localization.T("CustomWidget.AgentHub.Universal.Title"));

        (McpConfigFormat Format, string LabelKey)[] formats =
        [
            (McpConfigFormat.StandardJson, "CustomWidget.AgentHub.Format.StandardJson"),
            (McpConfigFormat.VsCodeJson, "CustomWidget.AgentHub.Format.VsCodeJson"),
            (McpConfigFormat.CodexToml, "CustomWidget.AgentHub.Format.CodexToml"),
            (McpConfigFormat.DeepSeekHarnessYaml, "CustomWidget.AgentHub.Format.DeepSeekHarnessYaml"),
            (McpConfigFormat.StreamableHttpJson, "CustomWidget.AgentHub.Format.StreamableHttp"),
            (McpConfigFormat.OpenCodeJson, "CustomWidget.AgentHub.Format.OpenCodeJson"),
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
        controlsRow.Children.Add(formatCombo);

        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var copyBtn = new Button
        {
            Style = (Style)Resources["AccentActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.CopyMcpConfig")
        };

        var injectFileBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Universal.InjectFileButton")
        };
        ToolTipService.SetToolTip(injectFileBtn, Localization.T("CustomWidget.AgentHub.Universal.InjectFileTip"));

        actionsPanel.Children.Add(copyBtn);
        actionsPanel.Children.Add(injectFileBtn);
        Grid.SetColumn(actionsPanel, 1);
        controlsRow.Children.Add(actionsPanel);
        genStack.Children.Add(controlsRow);

        var httpDisabledNotice = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Visibility = Visibility.Collapsed,
            Background = ResolveBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x1A, 0x80, 0x80, 0x80))
        };
        var noticeRow = new Grid { ColumnSpacing = 10 };
        noticeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        noticeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        noticeRow.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.Universal.HttpOffHint"),
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        var goHttpBtn = new Button
        {
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Content = Localization.T("CustomWidget.AgentHub.Universal.GoEnableHttp")
        };
        goHttpBtn.Click += (_, _) => navigateToHttpTab();
        Grid.SetColumn(goHttpBtn, 1);
        noticeRow.Children.Add(goHttpBtn);
        httpDisabledNotice.Child = noticeRow;
        genStack.Children.Add(httpDisabledNotice);

        var snippetBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MaxHeight = 210,
            CornerRadius = new CornerRadius(6)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(snippetBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(snippetBox, ScrollBarVisibility.Auto);

        void SyncSnippetPreview()
        {
            if ((formatCombo.SelectedItem as ComboBoxItem)?.Tag is McpConfigFormat fmt)
            {
                _selectedMcpFormat = fmt;
                snippetBox.Text = CustomWidgetAgentConnectorService.BuildMcpSnippet(fmt);
                httpDisabledNotice.Visibility = fmt == McpConfigFormat.StreamableHttpJson &&
                                                !CustomWidgetStudioPreferencesStore.Shared.AgentHttpEnabled
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        formatCombo.SelectionChanged += (_, _) => SyncSnippetPreview();
        SyncSnippetPreview();
        genStack.Children.Add(snippetBox);

        copyBtn.Click += async (_, _) =>
        {
            CopyToClipboard(snippetBox.Text);
            ShowBanner(
                InfoBarSeverity.Success,
                Localization.T("CustomWidget.AgentHub.CopyMcpSuccessHint"));
            await FlashCopiedButtonFeedbackAsync(copyBtn, Localization.T("CustomWidget.AgentHub.CopyMcpConfig"));
        };

        injectFileBtn.Click += async (_, _) =>
        {
            if (_ownerWindow == IntPtr.Zero)
            {
                return;
            }

            injectFileBtn.IsEnabled = false;
            try
            {
                string? picked = await FileOpenPickerService.PickSingleFileAsync(
                    _ownerWindow,
                    [".json", ".toml", ".yml", ".yaml"],
                    PickerLocationId.Desktop);
                if (string.IsNullOrWhiteSpace(picked))
                {
                    return;
                }

                CustomWidgetAgentConnectorService.InjectMcpIntoConfigFile(picked);
                CustomWidgetStudioPreferencesStore.Shared.AddCustomAgentConfigPath(picked);
                RebuildAgentHubCards();
                ShowBanner(
                    InfoBarSeverity.Success,
                    Localization.Format("CustomWidget.AgentHub.Universal.InjectSuccess", picked));
            }
            catch (Exception ex)
            {
                ShowBanner(InfoBarSeverity.Error, ex.Message);
            }
            finally
            {
                injectFileBtn.IsEnabled = true;
            }
        };

        generatorCard.Child = genStack;
        container.Children.Add(generatorCard);

        IReadOnlyList<string> customPaths = CustomWidgetStudioPreferencesStore.Shared.CustomAgentConfigPaths;
        if (customPaths.Count > 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = Localization.T("CustomWidget.AgentHub.Universal.CustomFilesHeader"),
                Style = (Style)Resources["SettingsSubsectionTitleTextStyle"],
                Margin = new Thickness(4, 4, 4, 0)
            });

            foreach (string customPath in customPaths)
            {
                string capturedPath = customPath;
                bool active = CustomWidgetAgentConnectorService.HasMcpServerInAnyConfigFile(capturedPath);

                var rowActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var reinjectBtn = new Button
                {
                    Style = (Style)Resources["ActionButtonStyle"],
                    Content = active
                        ? Localization.T("CustomWidget.AgentHub.ResyncConfig")
                        : Localization.T("CustomWidget.AgentHub.OneClickConnect")
                };
                reinjectBtn.Click += (_, _) =>
                {
                    try
                    {
                        CustomWidgetAgentConnectorService.InjectMcpIntoConfigFile(capturedPath);
                        RebuildAgentHubCards();
                        ShowBanner(
                            InfoBarSeverity.Success,
                            Localization.Format("CustomWidget.AgentHub.Universal.InjectSuccess", capturedPath));
                    }
                    catch (Exception ex)
                    {
                        ShowBanner(InfoBarSeverity.Error, ex.Message);
                    }
                };
                rowActions.Children.Add(reinjectBtn);

                var openBtn = new Button
                {
                    Style = (Style)Resources["IconActionButtonStyle"],
                    Content = new FontIcon { Glyph = "\uE8DA", FontSize = 13 }
                };
                ToolTipService.SetToolTip(openBtn, Localization.T("CustomWidget.AgentHub.RevealConfigShort"));
                openBtn.Click += (_, _) => CustomWidgetAgentConnectorService.RevealPathInExplorer(capturedPath);
                rowActions.Children.Add(openBtn);

                var removeBtn = new Button
                {
                    Style = (Style)Resources["IconActionButtonStyle"],
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }
                };
                ToolTipService.SetToolTip(removeBtn, Localization.T("Common.Delete"));
                removeBtn.Click += (_, _) =>
                {
                    CustomWidgetStudioPreferencesStore.Shared.RemoveCustomAgentConfigPath(capturedPath);
                    RebuildAgentHubCards();
                };
                rowActions.Children.Add(removeBtn);

                var fileCard = new SettingsCard
                {
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    Header = Path.GetFileName(capturedPath),
                    Description = $"{(active ? "✓ " + Localization.T("CustomWidget.AgentHub.PillConnected") : Localization.T("CustomWidget.AgentHub.PillAvailable"))} · {capturedPath}",
                    HeaderIcon = new FontIcon { Glyph = "\uE8A5" },
                    Content = rowActions
                };
                container.Children.Add(fileCard);
            }
        }
    }

    private void BuildSkillsAndWorkspaceTabContent(StackPanel container)
    {
        string widgetsRoot = CustomWidgetDataStore.GetWidgetsRootDirectory();

        var workspaceCard = new Border
        {
            Style = (Style)Resources["SettingsGroupStyle"],
            Padding = new Thickness(16, 14, 16, 14)
        };
        var wsStack = new StackPanel { Spacing = 10 };
        wsStack.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.Workspace.Title"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        wsStack.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.Workspace.Desc"),
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        });

        var pathBox = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Background = ResolveBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x16, 0x80, 0x80, 0x80)),
            Child = new TextBlock
            {
                Text = widgetsRoot,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            }
        };
        wsStack.Children.Add(pathBox);

        var wsButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var openEditorBtn = new Button
        {
            Style = (Style)Resources["AccentActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Workspace.OpenEditor")
        };
        openEditorBtn.Click += async (_, _) =>
        {
            try
            {
                await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(widgetsRoot);
                CustomWidgetAgentConnectorService.TryOpenWorkspaceInEditor(widgetsRoot, "auto");
            }
            catch (Exception ex)
            {
                if (_isSectionLoaded)
                {
                    ShowBanner(InfoBarSeverity.Error, ex.Message);
                }
            }
        };
        wsButtons.Children.Add(openEditorBtn);

        var openTerminalBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Workspace.OpenTerminal")
        };
        openTerminalBtn.Click += async (_, _) =>
        {
            try
            {
                await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(widgetsRoot);
                CustomWidgetAgentConnectorService.TryOpenWorkspaceInTerminal(widgetsRoot);
            }
            catch (Exception ex)
            {
                if (_isSectionLoaded)
                {
                    ShowBanner(InfoBarSeverity.Error, ex.Message);
                }
            }
        };
        wsButtons.Children.Add(openTerminalBtn);

        var openFolderBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.Studio.Menu.OpenDataFolder")
        };
        openFolderBtn.Click += async (_, _) =>
        {
            try
            {
                await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(widgetsRoot);
                CustomWidgetAgentConnectorService.RevealPathInExplorer(widgetsRoot);
            }
            catch (Exception ex)
            {
                if (_isSectionLoaded)
                {
                    ShowBanner(InfoBarSeverity.Error, ex.Message);
                }
            }
        };
        wsButtons.Children.Add(openFolderBtn);

        var copyCliCheatBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Workspace.CopyCliRef")
        };
        copyCliCheatBtn.Click += async (_, _) =>
        {
            string exe = CustomWidgetAgentConnectorService.ResolveDeskBoxExecutablePath();
            string cheat = $"""
                "{exe}" widget status
                "{exe}" widget list
                "{exe}" widget spec
                "{exe}" widget validate --file "<manifest.json>"
                "{exe}" widget apply --file "<manifest.json>"
                "{exe}" widget push --id agent-hud --set taskTitle="Build" --set statusText="Running" --set progress=68
                "{exe}" widget preview --id <widgetId> --out preview.png
                """;
            CopyToClipboard(cheat);
            await FlashCopiedButtonFeedbackAsync(copyCliCheatBtn, Localization.T("CustomWidget.AgentHub.Workspace.CopyCliRef"));
        };
        wsButtons.Children.Add(copyCliCheatBtn);

        wsStack.Children.Add(wsButtons);
        workspaceCard.Child = wsStack;
        container.Children.Add(workspaceCard);

        var skillsCard = new Border
        {
            Style = (Style)Resources["SettingsGroupStyle"],
            Padding = new Thickness(16, 14, 16, 14)
        };
        var skStack = new StackPanel { Spacing = 10 };

        var skHeaderGrid = new Grid { ColumnSpacing = 12 };
        skHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        skHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var skTitleStack = new StackPanel { Spacing = 2 };
        skTitleStack.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.Skills.Title"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        skTitleStack.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.Skills.Desc"),
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        });
        skHeaderGrid.Children.Add(skTitleStack);

        var skTopActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top
        };

        var syncAllSkillsBtn = new Button
        {
            Style = (Style)Resources["AccentActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Skills.SyncAllButton")
        };
        syncAllSkillsBtn.Click += async (_, _) =>
        {
            syncAllSkillsBtn.IsEnabled = false;
            try
            {
                int count = await CustomWidgetAgentConnectorService.SyncAllGlobalSkillsAsync();
                RebuildAgentHubCards();
                ShowBanner(
                    InfoBarSeverity.Success,
                    Localization.Format("CustomWidget.AgentHub.Skills.SyncedAllSuccess", count));
            }
            catch (Exception ex)
            {
                ShowBanner(InfoBarSeverity.Error, ex.Message);
                syncAllSkillsBtn.IsEnabled = true;
            }
        };
        skTopActions.Children.Add(syncAllSkillsBtn);

        var exportSkillBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Skills.ExportCustomButton")
        };
        exportSkillBtn.Click += async (_, _) =>
        {
            if (_ownerWindow == IntPtr.Zero)
            {
                return;
            }

            exportSkillBtn.IsEnabled = false;
            try
            {
                FolderPickResult picked = await FolderPickerService.PickFolderWithOutcomeAsync(
                    _ownerWindow,
                    "custom-widget-skill-export");
                if (picked.Outcome != FolderPickOutcome.Picked || string.IsNullOrWhiteSpace(picked.Folder))
                {
                    return;
                }

                string writtenSkillPath = await CustomWidgetAgentConnectorService.InstallAgentSkillAsync(picked.Folder);
                ShowBanner(
                    InfoBarSeverity.Success,
                    Localization.Format("CustomWidget.AgentHub.Skills.ExportSuccess", writtenSkillPath));
            }
            catch (Exception ex)
            {
                ShowBanner(InfoBarSeverity.Error, ex.Message);
            }
            finally
            {
                exportSkillBtn.IsEnabled = true;
            }
        };
        skTopActions.Children.Add(exportSkillBtn);

        Grid.SetColumn(skTopActions, 1);
        skHeaderGrid.Children.Add(skTopActions);
        skStack.Children.Add(skHeaderGrid);

        IReadOnlyList<AgentSkillLocationStatus> skillLocations =
            CustomWidgetAgentConnectorService.DetectGlobalSkillLocations();
        foreach (AgentSkillLocationStatus loc in skillLocations)
        {
            AgentSkillLocationStatus capturedLoc = loc;
            var locGrid = new Grid
            {
                Padding = new Thickness(10, 8, 10, 8),
                ColumnSpacing = 12,
                CornerRadius = new CornerRadius(6),
                Background = ResolveBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x12, 0x80, 0x80, 0x80))
            };
            locGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            locGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            locGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var locInfo = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            locInfo.Children.Add(new TextBlock
            {
                Text = capturedLoc.DisplayName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            });
            locInfo.Children.Add(new TextBlock
            {
                Text = capturedLoc.SkillFilePath,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ResolveBrush("TextFillColorSecondaryBrush", Color.FromArgb(0xA8, 0x80, 0x80, 0x80))
            });
            locGrid.Children.Add(locInfo);

            var stateTb = new TextBlock
            {
                Text = capturedLoc.IsInstalled
                    ? "● " + Localization.T("CustomWidget.AgentHub.PillConnected")
                    : Localization.T("CustomWidget.AgentHub.PillAvailable"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = capturedLoc.IsInstalled
                    ? ResolveBrush("SystemFillColorSuccessBrush", Color.FromArgb(0xFF, 0x10, 0x7C, 0x10))
                    : ResolveBrush("TextFillColorSecondaryBrush", Color.FromArgb(0xA8, 0x80, 0x80, 0x80))
            };
            Grid.SetColumn(stateTb, 1);
            locGrid.Children.Add(stateTb);

            var installOneBtn = new Button
            {
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                Content = capturedLoc.IsInstalled
                    ? Localization.T("CustomWidget.AgentHub.ResyncConfig")
                    : Localization.T("CustomWidget.AgentHub.Skills.InstallOne")
            };
            installOneBtn.Click += async (_, _) =>
            {
                installOneBtn.IsEnabled = false;
                try
                {
                    await CustomWidgetAgentConnectorService.InstallAgentSkillAsync(capturedLoc.SkillRootDirectory);
                    if (_isSectionLoaded)
                    {
                        RebuildAgentHubCards();
                    }
                }
                catch (Exception ex)
                {
                    if (_isSectionLoaded)
                    {
                        ShowBanner(InfoBarSeverity.Error, ex.Message);
                        installOneBtn.IsEnabled = true;
                    }
                }
            };
            Grid.SetColumn(installOneBtn, 2);
            locGrid.Children.Add(installOneBtn);

            skStack.Children.Add(locGrid);
        }

        skillsCard.Child = skStack;
        container.Children.Add(skillsCard);
    }

    private void BuildHttpBridgeTabContent(StackPanel container)
    {
        CustomWidgetStudioPreferencesStore prefs = CustomWidgetStudioPreferencesStore.Shared;
        bool isHttpEnabled = prefs.AgentHttpEnabled;
        int currentPort = prefs.AgentHttpPort;
        string currentToken = prefs.GetOrCreateAgentHttpToken();

        var toggleSwitch = new ToggleSwitch
        {
            IsOn = isHttpEnabled,
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0
        };

        var portNumberBox = new NumberBox
        {
            Minimum = 1024,
            Maximum = 65535,
            Value = currentPort,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 136
        };

        toggleSwitch.Toggled += async (_, _) =>
        {
            int targetPort = double.IsNaN(portNumberBox.Value)
                ? prefs.AgentHttpPort
                : Math.Clamp((int)Math.Round(portNumberBox.Value), 1024, 65535);
            await ApplyHttpBridgeConfigAsync(toggleSwitch.IsOn, targetPort);
        };

        var enableCard = new SettingsCard
        {
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Header = Localization.T("CustomWidget.AgentHub.Http.EnableHeader"),
            Description = Localization.T("CustomWidget.AgentHub.Http.EnableDesc"),
            HeaderIcon = new FontIcon { Glyph = "\uE774" },
            Content = toggleSwitch
        };
        container.Children.Add(enableCard);

        var portRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        portRow.Children.Add(portNumberBox);
        var applyPortBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Http.ApplyPortButton")
        };
        applyPortBtn.Click += async (_, _) =>
        {
            int targetPort = double.IsNaN(portNumberBox.Value)
                ? CustomWidgetStudioPreferencesStore.DefaultAgentHttpPort
                : Math.Clamp((int)Math.Round(portNumberBox.Value), 1024, 65535);
            portNumberBox.Value = targetPort;
            await ApplyHttpBridgeConfigAsync(toggleSwitch.IsOn, targetPort);
        };
        portRow.Children.Add(applyPortBtn);

        var portCard = new SettingsCard
        {
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Header = Localization.T("CustomWidget.AgentHub.Http.PortHeader"),
            Description = $"http://127.0.0.1:{currentPort}/mcp  ·  http://127.0.0.1:{currentPort}/api/v1/*",
            HeaderIcon = new FontIcon { Glyph = "\uE968" },
            Content = portRow
        };
        container.Children.Add(portCard);

        string maskedToken = currentToken.Length > 10
            ? $"{currentToken[..8]}••••••••{currentToken[^4..]}"
            : "••••••••";
        var tokenActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copyTokenBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Http.CopyTokenButton")
        };
        copyTokenBtn.Click += async (_, _) =>
        {
            CopyToClipboard(prefs.GetOrCreateAgentHttpToken());
            await FlashCopiedButtonFeedbackAsync(copyTokenBtn, Localization.T("CustomWidget.AgentHub.Http.CopyTokenButton"));
        };
        tokenActions.Children.Add(copyTokenBtn);

        var regenTokenBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Http.ResetTokenButton")
        };
        regenTokenBtn.Click += async (_, _) =>
        {
            try
            {
                prefs.RegenerateAgentHttpToken();
                if (prefs.AgentHttpEnabled)
                {
                    await ApplyHttpBridgeConfigAsync(true, prefs.AgentHttpPort);
                }
                else if (_isSectionLoaded)
                {
                    RebuildAgentHubCards();
                }

                if (_isSectionLoaded)
                {
                    ShowBanner(
                        InfoBarSeverity.Success,
                        Localization.T("CustomWidget.AgentHub.Http.TokenResetSuccess"));
                }
            }
            catch (Exception ex)
            {
                if (_isSectionLoaded)
                {
                    ShowBanner(InfoBarSeverity.Error, ex.Message);
                }
            }
        };
        tokenActions.Children.Add(regenTokenBtn);

        var tokenCard = new SettingsCard
        {
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Header = Localization.T("CustomWidget.AgentHub.Http.TokenHeader"),
            Description = $"{Localization.T("CustomWidget.AgentHub.Http.TokenDesc")} ({maskedToken})",
            HeaderIcon = new FontIcon { Glyph = "\uE8D7" },
            Content = tokenActions
        };
        container.Children.Add(tokenCard);

        var endpointsCard = new Border
        {
            Style = (Style)Resources["SettingsGroupStyle"],
            Padding = new Thickness(16, 14, 16, 14)
        };
        var epStack = new StackPanel { Spacing = 10 };
        epStack.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.Http.ExamplesTitle"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        epStack.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.Http.ExamplesDesc"),
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        });

        var epButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var copyMcpUrlBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Http.CopyMcpUrl")
        };
        copyMcpUrlBtn.Click += async (_, _) =>
        {
            CopyToClipboard($"http://127.0.0.1:{prefs.AgentHttpPort}/mcp");
            await FlashCopiedButtonFeedbackAsync(copyMcpUrlBtn, Localization.T("CustomWidget.AgentHub.Http.CopyMcpUrl"));
        };
        epButtons.Children.Add(copyMcpUrlBtn);

        var copyPwshBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Http.CopyPowerShell")
        };
        copyPwshBtn.Click += async (_, _) =>
        {
            string script = CustomWidgetAgentConnectorService.BuildHttpPushExample(
                prefs.AgentHttpPort,
                prefs.GetOrCreateAgentHttpToken(),
                powerShell: true);
            CopyToClipboard(script);
            await FlashCopiedButtonFeedbackAsync(copyPwshBtn, Localization.T("CustomWidget.AgentHub.Http.CopyPowerShell"));
        };
        epButtons.Children.Add(copyPwshBtn);

        var copyCurlBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Http.CopyCurl")
        };
        copyCurlBtn.Click += async (_, _) =>
        {
            string script = CustomWidgetAgentConnectorService.BuildHttpPushExample(
                prefs.AgentHttpPort,
                prefs.GetOrCreateAgentHttpToken(),
                powerShell: false);
            CopyToClipboard(script);
            await FlashCopiedButtonFeedbackAsync(copyCurlBtn, Localization.T("CustomWidget.AgentHub.Http.CopyCurl"));
        };
        epButtons.Children.Add(copyCurlBtn);

        epStack.Children.Add(epButtons);
        endpointsCard.Child = epStack;
        container.Children.Add(endpointsCard);
    }

    private async Task ApplyHttpBridgeConfigAsync(bool enabled, int port)
    {
        try
        {
            CustomWidgetStudioPreferencesStore prefs = CustomWidgetStudioPreferencesStore.Shared;
            prefs.SetAgentHttpPort(port);
            prefs.SetAgentHttpEnabled(enabled);
            _ = prefs.GetOrCreateAgentHttpToken();

            string? ipcResp = await DeskBoxAgentBridgeClient.TrySendAsync(w =>
            {
                w.WriteString("op", "http_configure");
                w.WriteBoolean("enabled", enabled);
                w.WriteNumber("port", port);
            });

            if (!_isSectionLoaded)
            {
                return;
            }

            RebuildAgentHubCards();

            if (enabled && string.IsNullOrWhiteSpace(ipcResp))
            {
                ShowBanner(
                    InfoBarSeverity.Warning,
                    Localization.T("CustomWidget.AgentHub.BridgeSubtitleOffline"));
            }
        }
        catch (Exception ex)
        {
            if (_isSectionLoaded)
            {
                ShowBanner(InfoBarSeverity.Error, ex.Message);
            }
        }
    }

    private FrameworkElement BuildPlaybooksAndHudSection()
    {
        var section = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };

        section.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.AgentHub.PlaybooksHeader"),
            Style = (Style)Resources["SettingsSubsectionTitleTextStyle"],
            Margin = new Thickness(4, 2, 4, 0)
        });

        var card = new Border
        {
            Style = (Style)Resources["SettingsGroupStyle"],
            Padding = new Thickness(16, 14, 16, 14)
        };
        var stack = new StackPanel { Spacing = 12 };

        var row1 = CreatePlaybookRowGrid();
        row1.Children.Add(CreatePlaybookTextBlock(
            "1",
            Localization.T("CustomWidget.AgentHub.Playbook.CreateTitle"),
            Localization.T("CustomWidget.AgentHub.Playbook.CreateDesc")));
        var copyCreateBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            Content = Localization.T("CustomWidget.AgentHub.Playbook.CopyCreate")
        };
        copyCreateBtn.Click += async (_, _) =>
        {
            try
            {
                await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(
                    CustomWidgetDataStore.GetWidgetsRootDirectory());
                string prompt = CustomWidgetAgentConnectorService.BuildKickoffPrompt(
                    AgentPlaybookKind.CreateNewWidget,
                    Localization.CurrentCultureName);
                CopyToClipboard(prompt);
                await FlashCopiedButtonFeedbackAsync(copyCreateBtn, Localization.T("CustomWidget.AgentHub.Playbook.CopyCreate"));
            }
            catch (Exception ex)
            {
                if (_isSectionLoaded)
                {
                    ShowBanner(InfoBarSeverity.Error, ex.Message);
                }
            }
        };
        Grid.SetColumn(copyCreateBtn, 1);
        row1.Children.Add(copyCreateBtn);
        stack.Children.Add(row1);

        stack.Children.Add(CreatePlaybookHairline());

        List<(string Id, string Name)> installedWidgets = DiscoverInstalledCustomWidgetsForPlaybook();
        var row2 = CreatePlaybookRowGrid();
        row2.Children.Add(CreatePlaybookTextBlock(
            "2",
            Localization.T("CustomWidget.AgentHub.Playbook.ModifyTitle"),
            Localization.T("CustomWidget.AgentHub.Playbook.ModifyDesc")));

        var row2Actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var widgetPicker = new ComboBox
        {
            MinWidth = 156,
            MaxWidth = 220,
            IsEnabled = installedWidgets.Count > 0
        };
        int selectedWidgetIdx = 0;
        for (int i = 0; i < installedWidgets.Count; i++)
        {
            widgetPicker.Items.Add(new ComboBoxItem
            {
                Content = installedWidgets[i].Name,
                Tag = installedWidgets[i]
            });
            if (string.Equals(installedWidgets[i].Id, _selectedPlaybookWidgetId, StringComparison.Ordinal))
            {
                selectedWidgetIdx = i;
            }
        }
        if (installedWidgets.Count > 0)
        {
            widgetPicker.SelectedIndex = selectedWidgetIdx;
            widgetPicker.SelectionChanged += (_, _) =>
            {
                if ((widgetPicker.SelectedItem as ComboBoxItem)?.Tag is ValueTuple<string, string> sel)
                {
                    _selectedPlaybookWidgetId = sel.Item1;
                }
            };
        }
        else
        {
            widgetPicker.PlaceholderText = Localization.T("CustomWidget.AgentHub.Playbook.NoWidgetsPlaceholder");
        }
        row2Actions.Children.Add(widgetPicker);

        var copyModifyBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            IsEnabled = installedWidgets.Count > 0,
            Content = Localization.T("CustomWidget.AgentHub.Playbook.CopyModify")
        };
        copyModifyBtn.Click += async (_, _) =>
        {
            if ((widgetPicker.SelectedItem as ComboBoxItem)?.Tag is not ValueTuple<string, string> sel)
            {
                return;
            }

            try
            {
                CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(sel.Item1);
                await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(store.WidgetDirectory, sel.Item1);
                string prompt = CustomWidgetAgentConnectorService.BuildKickoffPrompt(
                    AgentPlaybookKind.ModifyExistingWidget,
                    Localization.CurrentCultureName,
                    sel.Item1,
                    sel.Item2);
                CopyToClipboard(prompt);
                await FlashCopiedButtonFeedbackAsync(copyModifyBtn, Localization.T("CustomWidget.AgentHub.Playbook.CopyModify"));
            }
            catch (Exception ex)
            {
                if (_isSectionLoaded)
                {
                    ShowBanner(InfoBarSeverity.Error, ex.Message);
                }
            }
        };
        row2Actions.Children.Add(copyModifyBtn);

        var openSelectedEditorBtn = new Button
        {
            Style = (Style)Resources["IconActionButtonStyle"],
            IsEnabled = installedWidgets.Count > 0,
            Content = new FontIcon { Glyph = "\uE943", FontSize = 13 }
        };
        ToolTipService.SetToolTip(openSelectedEditorBtn, Localization.T("CustomWidget.Studio.Menu.OpenInEditor"));
        openSelectedEditorBtn.Click += async (_, _) =>
        {
            if ((widgetPicker.SelectedItem as ComboBoxItem)?.Tag is ValueTuple<string, string> sel)
            {
                try
                {
                    CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(sel.Item1);
                    await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(store.WidgetDirectory, sel.Item1);
                    CustomWidgetAgentConnectorService.TryOpenWorkspaceInEditor(store.WidgetDirectory, "auto");
                }
                catch (Exception ex)
                {
                    App.Log($"[AiSettings] Open in external editor failed: {ex.Message}");
                }
            }
        };
        row2Actions.Children.Add(openSelectedEditorBtn);

        Grid.SetColumn(row2Actions, 1);
        row2.Children.Add(row2Actions);
        stack.Children.Add(row2);

        stack.Children.Add(CreatePlaybookHairline());

        var row3 = CreatePlaybookRowGrid();
        row3.Children.Add(CreatePlaybookTextBlock(
            "3",
            Localization.T("CustomWidget.AgentHub.Playbook.HudTitle"),
            Localization.T("CustomWidget.AgentHub.Playbook.HudDesc")));

        var row3Actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var copyHudPromptBtn = new Button
        {
            Style = (Style)Resources["ActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.Playbook.CopyHud")
        };
        copyHudPromptBtn.Click += async (_, _) =>
        {
            string prompt = CustomWidgetAgentConnectorService.BuildKickoffPrompt(
                AgentPlaybookKind.PushLiveHudTelemetry,
                Localization.CurrentCultureName);
            CopyToClipboard(prompt);
            await FlashCopiedButtonFeedbackAsync(copyHudPromptBtn, Localization.T("CustomWidget.AgentHub.Playbook.CopyHud"));
        };
        row3Actions.Children.Add(copyHudPromptBtn);

        var addHudBtn = new Button
        {
            Style = (Style)Resources["AccentActionButtonStyle"],
            Content = Localization.T("CustomWidget.AgentHub.CreateHudButton")
        };
        addHudBtn.Click += async (_, _) =>
        {
            if (_widgetActions is null || !_widgetActions.IsHostAvailable)
            {
                return;
            }

            addHudBtn.IsEnabled = false;
            try
            {
                GlanceWidgetInstanceInfo created = await _widgetActions.CreateCustomWidgetAsync(
                    CustomWidgetTemplates.AgentHudTemplateId);

                var viewWidgetBtn = new Button
                {
                    CornerRadius = new CornerRadius(6),
                    Content = Localization.T("CustomWidget.Settings.Title")
                };
                viewWidgetBtn.Click += (_, _) =>
                {
                    AiStatusInfoBar.IsOpen = false;
                    NavigateToCustomWidgetRequested?.Invoke(this, created.Id);
                };

                ShowBanner(
                    InfoBarSeverity.Success,
                    Localization.Format("CustomWidget.Studio.Status.Created", created.Name),
                    viewWidgetBtn);

                if (CustomWidgetStudioPreferencesStore.Shared.LocateOnDesktopAfterCommit)
                {
                    await _widgetActions.LocateCustomWidgetAsync(created.Id);
                }

                RebuildAgentHubCards();
            }
            catch (Exception ex)
            {
                ShowBanner(InfoBarSeverity.Error, ex.Message);
            }
            finally
            {
                addHudBtn.IsEnabled = true;
            }
        };
        row3Actions.Children.Add(addHudBtn);

        Grid.SetColumn(row3Actions, 1);
        row3.Children.Add(row3Actions);
        stack.Children.Add(row3);

        card.Child = stack;
        section.Children.Add(card);
        return section;
    }

    private static Grid CreatePlaybookRowGrid()
    {
        var g = new Grid { ColumnSpacing = 14 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return g;
    }

    private FrameworkElement CreatePlaybookTextBlock(string stepNumber, string title, string description)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Background = ResolveBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
            Child = new TextBlock
            {
                Text = stepNumber,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        textStack.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(textStack, 1);
        row.Children.Add(textStack);
        return row;
    }

    private Border CreatePlaybookHairline() => new()
    {
        Height = 1,
        Background = ResolveBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(0x18, 0x80, 0x80, 0x80))
    };

    private List<(string Id, string Name)> DiscoverInstalledCustomWidgetsForPlaybook()
    {
        var list = new List<(string Id, string Name)>();
        try
        {
            string root = CustomWidgetDataStore.GetWidgetsRootDirectory();
            if (!Directory.Exists(root))
            {
                return list;
            }

            foreach (string dir in Directory.GetDirectories(root))
            {
                string folderName = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(folderName) ||
                    folderName.StartsWith('_') ||
                    folderName.StartsWith('.'))
                {
                    continue;
                }

                string manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                string displayName = folderName;
                try
                {
                    string raw = File.ReadAllText(manifestPath, Encoding.UTF8);
                    CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(raw);
                    if (parsed.Manifest is { } manifest)
                    {
                        string resolved = manifest.ResolveLocalizedName(Localization.CurrentCultureName);
                        if (!string.IsNullOrWhiteSpace(resolved))
                        {
                            displayName = resolved;
                        }
                    }
                }
                catch
                {
                }

                list.Add((folderName, displayName));
            }
        }
        catch
        {
        }

        return list;
    }

    private static Button CreateSubtleStripButton(string glyph, string text)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });

        var btn = new Button
        {
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(4),
            Content = panel
        };

        if (Application.Current?.Resources is { } res &&
            res.TryGetValue("SubtleButtonStyle", out object? subtleObj) &&
            subtleObj is Style subtleStyle)
        {
            btn.Style = subtleStyle;
        }
        else
        {
            btn.Background = new SolidColorBrush(Colors.Transparent);
            btn.BorderThickness = new Thickness(0);
        }

        return btn;
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
