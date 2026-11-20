using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.Studio;

internal sealed class StudioChannelOnboardingController : IDisposable
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private StudioSessionController _session => _shell.Session;

    private DropDownButton? _activeChannelSelectorButton;
    private FrameworkElement? _activeAgentEntryTarget;
    private Button? _activeQuickSetupButton;
    private readonly List<Action> _channelSelectorRefreshers = [];
    private TeachingTip? _activeTourTeachingTip;
    private McpConfigFormat _quickConnectFormat = McpConfigFormat.StandardJson;
    private int _onboardingTourRunId;
    private OnboardingOverlayKind _activeOnboardingOverlayKind;
    private StudioAiChannelKind _activeStepTourChannel;
    private int _activeStepTourStep = 1;
    private Action? _dismissActiveOnboardingOverlay;

    internal StudioChannelOnboardingController(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }

    internal DropDownButton? ActiveChannelSelectorButton { get => _activeChannelSelectorButton; set => _activeChannelSelectorButton = value; }
    internal FrameworkElement? ActiveAgentEntryTarget { get => _activeAgentEntryTarget; set => _activeAgentEntryTarget = value; }
    internal Button? ActiveQuickSetupButton { get => _activeQuickSetupButton; set => _activeQuickSetupButton = value; }

    internal void ResetUiDelegates()
    {
        _channelSelectorRefreshers.Clear();
        _activeChannelSelectorButton = null;
        _activeAgentEntryTarget = null;
        _activeQuickSetupButton = null;
    }

    internal bool IsOnboardingActive => _activeOnboardingOverlayKind != OnboardingOverlayKind.None;
    internal int OnboardingTourRunId { get => _onboardingTourRunId; set => _onboardingTourRunId = value; }
    internal OnboardingOverlayKind ActiveOnboardingOverlayKind { get => _activeOnboardingOverlayKind; set => _activeOnboardingOverlayKind = value; }
    internal StudioAiChannelKind ActiveStepTourChannel { get => _activeStepTourChannel; set => _activeStepTourChannel = value; }
    internal int ActiveStepTourStep { get => _activeStepTourStep; set => _activeStepTourStep = value; }
    internal Action? DismissActiveOnboardingOverlay { get => _dismissActiveOnboardingOverlay; set => _dismissActiveOnboardingOverlay = value; }

    internal void NotifyChannelSelectorsRefresh()
    {
        foreach (Action refresh in _channelSelectorRefreshers.ToArray())
        {
            try
            {
                refresh();
            }
            catch (Exception ex)
            {
                _hostBridge.Log($"[StudioOnboarding] Refresh channel selector failed: {ex.Message}");
            }
        }
    }

    internal async Task ShowAiConfigDialogInStudioAsync(string? providerId, Action? onSaved)
    {
        if (_shell.IsDialogOpen || _shell.IsClosed || _shell.ShellRootGrid.XamlRoot is null)
        {
            return;
        }

        _shell.IsDialogOpen = true;
        try
        {
            string? targetId = string.IsNullOrWhiteSpace(providerId)
                ? (string.IsNullOrWhiteSpace(_prefs.PreferredAiProviderId) ? null : _prefs.PreferredAiProviderId)
                : providerId;
            string? savedProvider = await CustomWidgetAiConfigDialogHelper.ShowAsync(
                _shell.ShellRootGrid.XamlRoot,
                StudioUiPrimitives.GetStudioExplicitTheme(_shell),
                _loc,
                targetId,
                hideThinkingOption: false);
            if (string.IsNullOrWhiteSpace(savedProvider))
            {
                return;
            }

            CustomWidgetAiProviderPreset savedPreset = DeskBoxAiService.FindProviderPreset(savedProvider);
            AiProviderCredentialEntry? entry = CustomWidgetAiCredentialStore.Shared.Get(savedProvider);
            if (DeskBoxAiService.IsProviderConfigured(savedPreset, entry))
            {
                _prefs.SetPreferredStudioChannel(CustomWidgetStudioPreferencesStore.ChannelDirect);
                _prefs.SetPreferredAiProviderId(savedProvider);
            }

            onSaved?.Invoke();
            NotifyChannelSelectorsRefresh();
            if (!_session.IsInPlaceGenerating)
            {
                if (_session.Page == StudioPage.Start)
                {
                    _shell.StartGallery.ShowStartPage(_session.StartDraftIdea);
                }
                else if (_session.Page == StudioPage.Preview)
                {
                    _shell.StageViewport.ShowPreviewPage(justLoaded: false);
                }
            }
        }
        finally
        {
            _shell.IsDialogOpen = false;
        }
    }

    internal DropDownButton CreateAiChannelSelectorButton(Action? onChannelChanged = null)
    {
        var button = new DropDownButton
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };

        void RefreshLabel()
        {
            StudioAiChannelKind activeChannel = _session.ResolveEffectiveStudioChannel(out string? directId);
            if (activeChannel == StudioAiChannelKind.AgentMcp)
            {
                string mcpText = _loc.T("CustomWidget.Workshop.Composer.MenuAgentMcp");
                button.Content = StudioUiPrimitives.CreateSmallIconLabel("\uE756", mcpText);
                button.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "AccentFillColorDefaultBrush");
                ToolTipService.SetToolTip(button, _loc.T("CustomWidget.Workshop.Composer.ChannelAgentMcpTooltip"));
                AutomationProperties.SetName(button, mcpText);
                onChannelChanged?.Invoke();
                return;
            }

            button.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (directId is not null)
            {
                _ = CustomWidgetAiGeneratorService.Shared.TryEnsureModelsFetchedOnceAsync(directId);
                ResolvedAiInvocation resolved = CustomWidgetAiGeneratorService.Shared.ResolveEffectiveStudioInvocation(directId);
                string shortModel = resolved.Model.Contains('/')
                    ? resolved.Model[(resolved.Model.LastIndexOf('/') + 1)..]
                    : resolved.Model;
                shortModel = StudioManifestInspector.TruncateTextElements(shortModel, 24);

                string baseLabel = StudioManifestInspector.FormatCleanChannelLabel(resolved.Preset.DisplayName, shortModel);
                AiProviderCredentialEntry? tipCred = CustomWidgetAiCredentialStore.Shared.Get(resolved.Preset.Id);
                string thinkingTipShort = _loc.T(DeskBoxAiService.ParseThinkingMode(tipCred?.ThinkingMode) switch
                {
                    AiThinkingMode.Enabled => "CustomWidget.AiConfig.ThinkingEnabledShort",
                    AiThinkingMode.Disabled => "CustomWidget.AiConfig.ThinkingDisabledShort",
                    _ => "CustomWidget.AiConfig.ThinkingAutoShort"
                });
                string fullTip = $"{resolved.Preset.DisplayName} · {resolved.Model} · {_loc.T("CustomWidget.AiConfig.ThinkingHeader")}: {thinkingTipShort}";
                button.Content = StudioUiPrimitives.CreateSmallIconLabel("\uE945", baseLabel);
                ToolTipService.SetToolTip(button, fullTip);
                AutomationProperties.SetName(button, fullTip);
            }
            else
            {
                string unconfigDirect = _loc.T("CustomWidget.Workshop.Composer.ChannelDirectUnconfigured");
                button.Content = StudioUiPrimitives.CreateSmallIconLabel("\uE945", unconfigDirect);
                ToolTipService.SetToolTip(button, _loc.T("CustomWidget.Workshop.Composer.ConfigKey"));
                AutomationProperties.SetName(button, unconfigDirect);
            }

            onChannelChanged?.Invoke();
        }

        _channelSelectorRefreshers.Add(RefreshLabel);
        button.Loaded += (_, _) =>
        {
            if (!_channelSelectorRefreshers.Contains(RefreshLabel))
            {
                _channelSelectorRefreshers.Add(RefreshLabel);
            }
        };
        button.Unloaded += (_, _) => _channelSelectorRefreshers.Remove(RefreshLabel);

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            StudioAiChannelKind activeChannel = _session.ResolveEffectiveStudioChannel(out string? activeDirectId);

            void SwitchChannelFromSelector(StudioAiChannelKind targetKind)
            {
                string wire = targetKind == StudioAiChannelKind.AgentMcp
                    ? CustomWidgetStudioPreferencesStore.ChannelAgentMcp
                    : CustomWidgetStudioPreferencesStore.ChannelDirect;
                _prefs.SetPreferredStudioChannel(wire);
                _prefs.MarkAgentMcpGuideSeen();
                NotifyChannelSelectorsRefresh();
                _shell.ChatPanel.RefreshInPlaceGenerationChrome(refreshStream: false);
            }

            (StudioAiChannelKind Kind, string TitleKey, string Glyph)[] modeChoices =
            [
                (StudioAiChannelKind.DirectApi, "CustomWidget.Workshop.Onboarding.ModeDirectTitle", "\uE945"),
                (StudioAiChannelKind.AgentMcp, "CustomWidget.Workshop.Onboarding.ModeAgentTitle", "\uE756")
            ];
            foreach ((StudioAiChannelKind modeKind, string titleKey, string glyph) in modeChoices)
            {
                StudioAiChannelKind capturedKind = modeKind;
                var modeRadio = new RadioMenuFlyoutItem
                {
                    GroupName = "StudioChannelModeGroup",
                    Text = _loc.T(titleKey),
                    IsChecked = activeChannel == capturedKind,
                    Icon = new FontIcon { Glyph = glyph }
                };
                modeRadio.Click += async (_, _) =>
                {
                    try
                    {
                        SwitchChannelFromSelector(capturedKind);
                        if (capturedKind == StudioAiChannelKind.DirectApi &&
                            !CustomWidgetAiGeneratorService.Shared.HasAnyConfiguredProvider())
                        {
                            await ShowAiConfigDialogInStudioAsync(null, NotifyChannelSelectorsRefresh);
                        }
                    }
                    catch (Exception ex)
                    {
                        _hostBridge.Log($"[StudioOnboarding] Mode switch failed: {ex.Message}");
                    }
                };
                flyout.Items.Add(modeRadio);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());

            if (activeChannel == StudioAiChannelKind.DirectApi)
            {
                IReadOnlyList<CustomWidgetAiProviderPreset> configured =
                    CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders();

                if (configured.Count > 0)
                {
                    foreach (CustomWidgetAiProviderPreset provider in configured)
                    {
                        CustomWidgetAiProviderPreset captured = provider;
                        bool isActive = string.Equals(captured.Id, activeDirectId, StringComparison.OrdinalIgnoreCase);
                        ResolvedAiInvocation provResolved = CustomWidgetAiGeneratorService.Shared.ResolveEffectiveStudioInvocation(captured.Id);
                        var item = new ToggleMenuFlyoutItem
                        {
                            Text = $"{captured.DisplayName} ({provResolved.Model})",
                            IsChecked = isActive,
                            Icon = new FontIcon { Glyph = "\uE945" }
                        };
                        item.Click += (_, _) =>
                        {
                            _prefs.SetPreferredStudioChannel(CustomWidgetStudioPreferencesStore.ChannelDirect);
                            _prefs.SetPreferredAiProviderId(captured.Id);
                            NotifyChannelSelectorsRefresh();
                        };
                        flyout.Items.Add(item);
                    }

                    if (activeDirectId is not null)
                    {
                        CustomWidgetAiProviderPreset activePreset = CustomWidgetAiGeneratorService.FindProviderPreset(activeDirectId);
                        ResolvedAiInvocation activeResolved = CustomWidgetAiGeneratorService.Shared.ResolveEffectiveStudioInvocation(activePreset.Id);

                        var modelSub = new MenuFlyoutSubItem
                        {
                            Text = _loc.Format("CustomWidget.Workshop.Composer.ModelMenu", activeResolved.Model),
                            Icon = new FontIcon { Glyph = "\uE950" }
                        };
                        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string m in CustomWidgetAiGeneratorService.Shared.GetSelectableModels(activePreset.Id))
                        {
                            if (seenModels.Add(m))
                            {
                                AddModelChoiceItem(modelSub, activePreset, m, activeResolved.Model, NotifyChannelSelectorsRefresh);
                            }
                        }
                        modelSub.Items.Add(new MenuFlyoutSeparator());

                        var fetchModelsItem = new MenuFlyoutItem
                        {
                            Text = _loc.T("CustomWidget.AiConfig.FetchModels"),
                            Icon = new FontIcon { Glyph = "\uE72C" }
                        };
                        fetchModelsItem.Click += async (_, _) =>
                        {
                            try
                            {
                                AiModelListResult listRes = await CustomWidgetAiGeneratorService.Shared.FetchAvailableModelsAsync(
                                    activePreset.Id,
                                    persistDiscoveredModels: true);
                                if (listRes.Success && listRes.Models.Count > 0)
                                {
                                    _shell.ShowPageMessage(
                                        InfoBarSeverity.Success,
                                        string.Empty,
                                        _loc.Format("CustomWidget.AiConfig.FetchModelsSuccess", listRes.Models.Count),
                                        null);
                                }
                                else if (!string.IsNullOrWhiteSpace(listRes.ErrorMessage))
                                {
                                    _shell.ShowPageMessage(InfoBarSeverity.Warning, string.Empty, listRes.ErrorMessage, null);
                                }
                                NotifyChannelSelectorsRefresh();
                            }
                            catch (Exception ex)
                            {
                                _hostBridge.Log($"[StudioOnboarding] Fetch models failed: {ex.Message}");
                            }
                        };
                        modelSub.Items.Add(fetchModelsItem);

                        var customModelItem = new MenuFlyoutItem
                        {
                            Text = _loc.T("CustomWidget.Workshop.Composer.MoreModels"),
                            Icon = new FontIcon { Glyph = "\uE713" }
                        };
                        customModelItem.Click += async (_, _) =>
                        {
                            try
                            {
                                await ShowAiConfigDialogInStudioAsync(activePreset.Id, NotifyChannelSelectorsRefresh);
                            }
                            catch (Exception ex)
                            {
                                _hostBridge.Log($"[StudioOnboarding] Open AI config failed: {ex.Message}");
                            }
                        };
                        modelSub.Items.Add(customModelItem);
                        flyout.Items.Add(modelSub);

                        AiProviderCredentialEntry? thinkingCred = CustomWidgetAiCredentialStore.Shared.Get(activePreset.Id);
                        bool canSwapDeepSeek = AiProviderCatalog.SupportsClassicDeepSeekModelSwap(activePreset, thinkingCred?.FetchedModels) &&
                                               (string.Equals(activeResolved.Model, "deepseek-chat", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(activeResolved.Model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase));
                        bool lockedReasoningOnly = !canSwapDeepSeek && AiProviderCatalog.IsReasoningOnlyModel(activeResolved.Model);
                        AiThinkingMode currentThinking = lockedReasoningOnly
                            ? AiThinkingMode.Enabled
                            : DeskBoxAiService.ParseThinkingMode(thinkingCred?.ThinkingMode);
                        string currentThinkingShort = _loc.T(currentThinking switch
                        {
                            AiThinkingMode.Enabled => "CustomWidget.AiConfig.ThinkingEnabledShort",
                            AiThinkingMode.Disabled => "CustomWidget.AiConfig.ThinkingDisabledShort",
                            _ => "CustomWidget.AiConfig.ThinkingAutoShort"
                        });

                        var thinkingSub = new MenuFlyoutSubItem
                        {
                            Text = _loc.Format("CustomWidget.Workshop.Composer.ThinkingMenu", currentThinkingShort),
                            Icon = new FontIcon { Glyph = "\uEA80" }
                        };
                        (AiThinkingMode Mode, string LabelKey)[] thinkingChoices =
                        [
                            (AiThinkingMode.Auto, "CustomWidget.AiConfig.ThinkingAutoShort"),
                            (AiThinkingMode.Enabled, "CustomWidget.AiConfig.ThinkingEnabledShort"),
                            (AiThinkingMode.Disabled, "CustomWidget.AiConfig.ThinkingDisabledShort")
                        ];
                        foreach ((AiThinkingMode tMode, string labelKey) in thinkingChoices)
                        {
                            AiThinkingMode capturedMode = tMode;
                            var thinkingRadio = new RadioMenuFlyoutItem
                            {
                                GroupName = "StudioThinkingModeGroup",
                                Text = _loc.T(labelKey),
                                IsChecked = currentThinking == capturedMode,
                                IsEnabled = !lockedReasoningOnly
                            };
                            thinkingRadio.Click += (_, _) =>
                            {
                                CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(
                                    activePreset.Id,
                                    thinkingMode: DeskBoxAiService.ToThinkingWireValue(capturedMode));
                                DeskBoxAiService.Shared.ClearRouteFallbacks();
                                NotifyChannelSelectorsRefresh();
                            };
                            thinkingSub.Items.Add(thinkingRadio);
                        }
                        flyout.Items.Add(thinkingSub);
                    }
                }
                else
                {
                    var unconfigDirectItem = new MenuFlyoutItem
                    {
                        Text = _loc.T("CustomWidget.Workshop.Composer.ChannelDirectUnconfigured"),
                        Icon = new FontIcon { Glyph = "\uE945" }
                    };
                    unconfigDirectItem.Click += async (_, _) =>
                    {
                        try
                        {
                            await ShowAiConfigDialogInStudioAsync(null, NotifyChannelSelectorsRefresh);
                        }
                        catch (Exception ex)
                        {
                            _hostBridge.Log($"[StudioOnboarding] Open AI config failed: {ex.Message}");
                        }
                    };
                    flyout.Items.Add(unconfigDirectItem);
                }

                flyout.Items.Add(new MenuFlyoutSeparator());
                var keyItem = new MenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.Workshop.Composer.ConfigKey"),
                    Icon = new FontIcon { Glyph = "\uE8D7" }
                };
                keyItem.Click += async (_, _) =>
                {
                    try
                    {
                        await ShowAiConfigDialogInStudioAsync(activeDirectId, NotifyChannelSelectorsRefresh);
                    }
                    catch (Exception ex)
                    {
                        _hostBridge.Log($"[StudioOnboarding] Open AI config failed: {ex.Message}");
                    }
                };
                flyout.Items.Add(keyItem);
            }
            else if (activeChannel == StudioAiChannelKind.AgentMcp)
            {
                ConnectedMcpClientInfo? lastClient = DeskBoxAgentBridgeServer.LastMcpClient;
                flyout.Items.Add(new MenuFlyoutItem
                {
                    Text = lastClient is not null
                        ? _loc.Format("CustomWidget.Workshop.Composer.McpLastClientFormat", lastClient.ClientName)
                        : _loc.T("CustomWidget.Workshop.Composer.McpBridgeReady"),
                    IsEnabled = false,
                    Icon = new FontIcon { Glyph = lastClient is not null ? "\uE73E" : "\uE943" }
                });
                flyout.Items.Add(new MenuFlyoutSeparator());

                if (CustomWidgetAgentConnectorService.TryFindCursorExecutable() is not null)
                {
                    var installCursorItem = new MenuFlyoutItem
                    {
                        Text = _loc.T("CustomWidget.Workshop.Composer.InstallCursorMcp"),
                        Icon = new FontIcon { Glyph = "\uE8A7" }
                    };
                    installCursorItem.Click += (_, _) => CustomWidgetAgentConnectorService.TryLaunchMcpInstallDeepLink("cursor");
                    flyout.Items.Add(installCursorItem);
                }

                if (CustomWidgetAgentConnectorService.TryFindVsCodeExecutable() is not null)
                {
                    var installVsCodeItem = new MenuFlyoutItem
                    {
                        Text = _loc.T("CustomWidget.Workshop.Composer.InstallVsCodeMcp"),
                        Icon = new FontIcon { Glyph = "\uE8A7" }
                    };
                    installVsCodeItem.Click += (_, _) => CustomWidgetAgentConnectorService.TryLaunchMcpInstallDeepLink("vscode");
                    flyout.Items.Add(installVsCodeItem);
                }

                IReadOnlyList<DetectedCodeEditorInfo> detectedEditors = CustomWidgetAgentConnectorService.DetectInstalledCodeEditors();
                if (detectedEditors.Count > 0)
                {
                    string curPrefEditor = _prefs.PreferredCodeEditor;
                    string curEditorLabel = string.Equals(curPrefEditor, CustomWidgetStudioPreferencesStore.EditorAuto, StringComparison.OrdinalIgnoreCase)
                        ? _loc.T("CustomWidget.Workshop.Composer.EditorAuto")
                        : (detectedEditors.FirstOrDefault(e => string.Equals(e.Id, curPrefEditor, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? curPrefEditor);
                    var editorSub = new MenuFlyoutSubItem
                    {
                        Text = _loc.Format("CustomWidget.Workshop.Composer.PreferredEditorFormat", curEditorLabel),
                        Icon = new FontIcon { Glyph = "\uE8A5" }
                    };

                    var autoEditorItem = new RadioMenuFlyoutItem
                    {
                        GroupName = "PreferredCodeEditorGroup",
                        Text = _loc.T("CustomWidget.Workshop.Composer.EditorAuto"),
                        IsChecked = string.Equals(curPrefEditor, CustomWidgetStudioPreferencesStore.EditorAuto, StringComparison.OrdinalIgnoreCase)
                    };
                    autoEditorItem.Click += (_, _) => _prefs.SetPreferredCodeEditor(CustomWidgetStudioPreferencesStore.EditorAuto);
                    editorSub.Items.Add(autoEditorItem);

                    foreach (DetectedCodeEditorInfo editorInfo in detectedEditors)
                    {
                        DetectedCodeEditorInfo capturedEditor = editorInfo;
                        var edItem = new RadioMenuFlyoutItem
                        {
                            GroupName = "PreferredCodeEditorGroup",
                            Text = capturedEditor.DisplayName,
                            IsChecked = string.Equals(curPrefEditor, capturedEditor.Id, StringComparison.OrdinalIgnoreCase)
                        };
                        edItem.Click += (_, _) => _prefs.SetPreferredCodeEditor(capturedEditor.Id);
                        editorSub.Items.Add(edItem);
                    }
                    flyout.Items.Add(editorSub);
                }

                var copySetupPromptItem = new MenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.AgentHub.Playbook.CopyCreate"),
                    Icon = new FontIcon { Glyph = "\uE8C8" }
                };
                copySetupPromptItem.Click += (_, _) => _shell.SetClipboardText(CustomWidgetAgentConnectorService.BuildMcpSetupPrompt(_loc.CurrentCultureName));
                flyout.Items.Add(copySetupPromptItem);

                var mcpConfigItem = new MenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.Workshop.Composer.ManageMcpClients"),
                    Icon = new FontIcon { Glyph = "\uE943" }
                };
                mcpConfigItem.Click += async (_, _) =>
                {
                    try
                    {
                        await ShowAgentMcpQuickConnectDialogAsync(NotifyChannelSelectorsRefresh);
                    }
                    catch (Exception ex)
                    {
                        _hostBridge.Log($"[StudioOnboarding] Open MCP quick connect failed: {ex.Message}");
                    }
                };
                flyout.Items.Add(mcpConfigItem);
            }
        };

        button.Flyout = flyout;
        RefreshLabel();
        return button;
    }

    private static void AddModelChoiceItem(
        MenuFlyoutSubItem parent,
        CustomWidgetAiProviderPreset preset,
        string modelId,
        string activeModelId,
        Action refreshLabel)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = modelId,
            IsChecked = string.Equals(modelId, activeModelId, StringComparison.OrdinalIgnoreCase)
        };
        item.Click += (_, _) =>
        {
            CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(preset.Id, customModel: modelId);
            DeskBoxAiService.Shared.ClearRouteFallbacks();
            refreshLabel();
        };
        parent.Items.Add(item);
    }

    internal void StopOnboardingAnimationTimer()
    {
        if (_activeTourTeachingTip is not null)
        {
            _activeTourTeachingTip.IsOpen = false;
            _shell.ShellRootGrid.Children.Remove(_activeTourTeachingTip);
            _activeTourTeachingTip = null;
        }
    }

    internal void CloseOnboardingOverlay()
    {
        StopOnboardingAnimationTimer();
        _dismissActiveOnboardingOverlay = null;
        _activeOnboardingOverlayKind = OnboardingOverlayKind.None;
        _session.PendingChannelSelectionIdea = null;
    }

    private void ApplySelectedStudioChannel(StudioAiChannelKind channel)
    {
        string prefValue = channel == StudioAiChannelKind.AgentMcp
            ? CustomWidgetStudioPreferencesStore.ChannelAgentMcp
            : CustomWidgetStudioPreferencesStore.ChannelDirect;
        _prefs.SetPreferredStudioChannel(prefValue);
        NotifyChannelSelectorsRefresh();
        _shell.ChatPanel.RefreshInPlaceGenerationChrome(refreshStream: false);
    }

    internal void ShowAgentMcpOnboardingTour(bool forceShow = false)
    {
        if (_shell.IsClosed || (!forceShow && _prefs.HasSeenAgentMcpGuide))
        {
            return;
        }

        int runId = ++_onboardingTourRunId;
        _ = OpenChannelSelectionModalAsync(runId);
    }

    private async Task OpenChannelSelectionModalAsync(int runId)
    {
        await Task.Delay(30);
        if (_shell.IsClosed || _shell.IsDialogOpen || runId != _onboardingTourRunId || _shell.ShellRootGrid.XamlRoot is null)
        {
            return;
        }

        CloseOnboardingOverlay();
        _activeOnboardingOverlayKind = OnboardingOverlayKind.ChannelModal;
        _shell.IsDialogOpen = true;

        bool hasConfiguredDirect = CustomWidgetAiGeneratorService.Shared.HasAnyConfiguredProvider();
        StudioAiChannelKind selectedChannel = string.IsNullOrWhiteSpace(_prefs.PreferredStudioChannel)
            ? (hasConfiguredDirect ? StudioAiChannelKind.DirectApi : StudioAiChannelKind.AgentMcp)
            : _session.ResolveEffectiveStudioChannel(out _);

        ElementTheme theme = StudioUiPrimitives.GetStudioExplicitTheme(_shell);
        Windows.UI.Color accent = StudioUiPrimitives.ResolveAccentColor(_shell);
        bool isDark = theme == ElementTheme.Dark;

        var rootStack = new StackPanel { Spacing = 12, MinWidth = 460, MaxWidth = 520 };
        rootStack.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Onboarding.Subtitle"),
            FontSize = 12.5,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        var cardsStack = new StackPanel { Spacing = 8 };
        Border directCard = null!;
        Border agentCard = null!;
        RadioButton directRadio = null!;
        RadioButton agentRadio = null!;

        Border BuildModeOptionCard(
            StudioAiChannelKind kind,
            string glyph,
            string titleKey,
            string badgeText,
            string hintKey,
            string step1Key,
            string step2Key,
            string step3Key,
            out RadioButton radioOut)
        {
            var radio = new RadioButton
            {
                GroupName = "StudioChannelChooserDialogGroup",
                IsChecked = selectedChannel == kind,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Top
            };
            radioOut = radio;

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
            titleRow.Children.Add(new TextBlock
            {
                Text = _loc.T(titleKey),
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            titleRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
                Child = new TextBlock
                {
                    Text = badgeText,
                    FontSize = 11,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush")
                }
            });

            var stepsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (string sk in new[] { step1Key, step2Key, step3Key })
            {
                stepsRow.Children.Add(new TextBlock
                {
                    Text = _loc.T(sk),
                    FontSize = 11,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush")
                });
            }

            var contentCol = new StackPanel { Spacing = 4 };
            contentCol.Children.Add(titleRow);
            contentCol.Children.Add(new TextBlock
            {
                Text = _loc.T(hintKey),
                FontSize = 12,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            contentCol.Children.Add(stepsRow);

            var rowGrid = new Grid { ColumnSpacing = 8 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.Children.Add(radio);
            Grid.SetColumn(contentCol, 1);
            rowGrid.Children.Add(contentCol);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                Child = rowGrid
            };
            return card;
        }

        string directBadge = _loc.T(hasConfiguredDirect
            ? "CustomWidget.Workshop.Onboarding.ApiKeyReadyBadge"
            : "CustomWidget.Workshop.Onboarding.ApiKeyUnconfiguredBadge");
        string agentBadge = DeskBoxAgentBridgeServer.LastMcpClient is { } lastClient
            ? _loc.Format("CustomWidget.Workshop.Onboarding.DetectedAgentBadgeFormat", lastClient.ClientName)
            : _loc.T("CustomWidget.Workshop.Onboarding.ModeAgentBadge");

        directCard = BuildModeOptionCard(
            StudioAiChannelKind.DirectApi,
            "\uE945",
            "CustomWidget.Workshop.Onboarding.ModeDirectTitle",
            directBadge,
            "CustomWidget.Workshop.Onboarding.ModeDirectHint",
            "CustomWidget.Workshop.Onboarding.ModeDirectAnim1",
            "CustomWidget.Workshop.Onboarding.ModeDirectAnim2",
            "CustomWidget.Workshop.Onboarding.ModeDirectAnim3",
            out directRadio);
        agentCard = BuildModeOptionCard(
            StudioAiChannelKind.AgentMcp,
            "\uE756",
            "CustomWidget.Workshop.Onboarding.ModeAgentTitle",
            agentBadge,
            "CustomWidget.Workshop.Onboarding.ModeAgentHint",
            "CustomWidget.Workshop.Onboarding.ModeAgentAnim1",
            "CustomWidget.Workshop.Onboarding.ModeAgentAnim2",
            "CustomWidget.Workshop.Onboarding.ModeAgentAnim3",
            out agentRadio);

        ContentDialog? dialog = null;
        void RefreshCardSelection(StudioAiChannelKind chosen)
        {
            selectedChannel = chosen;
            directRadio.IsChecked = chosen == StudioAiChannelKind.DirectApi;
            agentRadio.IsChecked = chosen == StudioAiChannelKind.AgentMcp;
            foreach ((Border card, bool active) in new[] { (directCard, chosen == StudioAiChannelKind.DirectApi), (agentCard, chosen == StudioAiChannelKind.AgentMcp) })
            {
                card.BorderBrush = active
                    ? new SolidColorBrush(accent)
                    : StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush");
                card.Background = active
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(isDark ? 32 : 20), accent.R, accent.G, accent.B))
                    : StudioUiPrimitives.ThemeBrush(_shell, "CardBackgroundFillColorDefaultBrush");
            }
            if (dialog is not null)
            {
                string modeTitle = _loc.T(chosen == StudioAiChannelKind.AgentMcp
                    ? "CustomWidget.Workshop.Onboarding.ModeAgentTitle"
                    : "CustomWidget.Workshop.Onboarding.ModeDirectTitle");
                dialog.SecondaryButtonText = _loc.Format("CustomWidget.Workshop.Onboarding.StartTourButton", modeTitle);
            }
        }

        directRadio.Checked += (_, _) => RefreshCardSelection(StudioAiChannelKind.DirectApi);
        agentRadio.Checked += (_, _) => RefreshCardSelection(StudioAiChannelKind.AgentMcp);
        directCard.Tapped += (_, _) => RefreshCardSelection(StudioAiChannelKind.DirectApi);
        agentCard.Tapped += (_, _) => RefreshCardSelection(StudioAiChannelKind.AgentMcp);

        cardsStack.Children.Add(directCard);
        cardsStack.Children.Add(agentCard);
        rootStack.Children.Add(cardsStack);

        dialog = new ContentDialog
        {
            XamlRoot = _shell.ShellRootGrid.XamlRoot,
            RequestedTheme = theme,
            Title = _loc.T("CustomWidget.Workshop.Onboarding.Title"),
            Content = rootStack,
            PrimaryButtonText = _loc.T("CustomWidget.Workshop.Onboarding.SkipTourButton"),
            CloseButtonText = _loc.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (Application.Current?.Resources is { } res &&
            res.TryGetValue("DefaultContentDialogStyle", out object? dStyle) &&
            dStyle is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }
        RefreshCardSelection(selectedChannel);

        ContentDialogResult result;
        string? pendingIdeaBeforeClose = _session.PendingChannelSelectionIdea;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[StudioOnboarding] Channel selection modal failed: {ex.Message}");
            return;
        }
        finally
        {
            _shell.IsDialogOpen = false;
            _activeOnboardingOverlayKind = OnboardingOverlayKind.None;
        }

        if (result == ContentDialogResult.None)
        {
            _session.PendingChannelSelectionIdea = null;
            return;
        }

        _prefs.MarkAgentMcpGuideSeen();
        ApplySelectedStudioChannel(selectedChannel);
        _session.PendingChannelSelectionIdea = null;

        if (result == ContentDialogResult.Secondary)
        {
            StartChannelStepTour(selectedChannel);
            return;
        }

        if (selectedChannel == StudioAiChannelKind.DirectApi &&
            !CustomWidgetAiGeneratorService.Shared.HasAnyConfiguredProvider())
        {
            await ShowAiConfigDialogInStudioAsync(null, NotifyChannelSelectorsRefresh);
            if (!string.IsNullOrWhiteSpace(pendingIdeaBeforeClose) &&
                CustomWidgetAiGeneratorService.Shared.HasAnyConfiguredProvider())
            {
                await _shell.Generation.BeginGenerationAsync(pendingIdeaBeforeClose);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(pendingIdeaBeforeClose))
        {
            await _shell.Generation.BeginGenerationAsync(pendingIdeaBeforeClose);
        }
    }

    internal void StartChannelStepTour(StudioAiChannelKind channel)
    {
        if (_shell.IsClosed)
        {
            return;
        }

        int runId = ++_onboardingTourRunId;
        _ = RunChannelStepTourAsync(runId, channel, initialStep: 1);
    }

    internal Task RunChannelStepTourAsync(int runId, StudioAiChannelKind channel, int initialStep = 1)
    {
        if (_shell.IsClosed || runId != _onboardingTourRunId)
        {
            return Task.CompletedTask;
        }

        StopOnboardingAnimationTimer();
        _activeOnboardingOverlayKind = OnboardingOverlayKind.StepTour;
        _activeStepTourChannel = channel;

        var tip = new TeachingTip
        {
            RequestedTheme = StudioUiPrimitives.GetStudioExplicitTheme(_shell),
            IsLightDismissEnabled = true,
            ShouldConstrainToRootBounds = true,
            PreferredPlacement = TeachingTipPlacementMode.Top
        };
        _activeTourTeachingTip = tip;
        _shell.ShellRootGrid.Children.Add(tip);

        bool isTransitioningStep = false;

        TeachingTipPlacementMode ResolveOptimalPlacement(FrameworkElement? anchor)
        {
            if (anchor is not { IsLoaded: true } || _shell.ShellRootGrid.ActualHeight <= 0)
            {
                return TeachingTipPlacementMode.Top;
            }

            try
            {
                Windows.Foundation.Point topLeft = anchor.TransformToVisual(_shell.ShellRootGrid)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                double rootH = _shell.ShellRootGrid.ActualHeight;
                double spaceAbove = topLeft.Y;
                double spaceBelow = rootH - (topLeft.Y + anchor.ActualHeight);
                // Prefer Top whenever the anchor is in the lower portion of the window or lacks room below.
                if (spaceBelow < 240 || spaceAbove >= spaceBelow)
                {
                    return TeachingTipPlacementMode.Top;
                }

                return TeachingTipPlacementMode.Bottom;
            }
            catch
            {
                return TeachingTipPlacementMode.Top;
            }
        }

        void FinishTour()
        {
            _prefs.MarkAgentMcpGuideSeen();
            CloseOnboardingOverlay();
        }

        _dismissActiveOnboardingOverlay = FinishTour;

        void ApplyStepContent(int step)
        {
            _activeStepTourStep = Math.Clamp(step, 1, 3);
            bool isAgent = channel == StudioAiChannelKind.AgentMcp;
            (string titleKey, string subKey, FrameworkElement? anchor) = _activeStepTourStep switch
            {
                1 => (isAgent ? "CustomWidget.Workshop.AgentMcp.Tour1Title" : "CustomWidget.Workshop.Onboarding.Direct1Title",
                      isAgent ? "CustomWidget.Workshop.AgentMcp.Tour1Subtitle" : "CustomWidget.Workshop.Onboarding.Direct1Subtitle",
                      (FrameworkElement?)_activeQuickSetupButton ?? _activeChannelSelectorButton),
                2 => (isAgent ? "CustomWidget.Workshop.AgentMcp.Tour2Title" : "CustomWidget.Workshop.Onboarding.Direct2Title",
                      isAgent ? "CustomWidget.Workshop.AgentMcp.Tour2Subtitle" : "CustomWidget.Workshop.Onboarding.Direct2Subtitle",
                      (FrameworkElement?)_shell.ComposerBuilder.StartIdeaTextBox ?? _shell.ChatPanel.RefineTextBox),
                _ => (isAgent ? "CustomWidget.Workshop.AgentMcp.Tour3Title" : "CustomWidget.Workshop.Onboarding.Direct3Title",
                      isAgent ? "CustomWidget.Workshop.AgentMcp.Tour3Subtitle" : "CustomWidget.Workshop.Onboarding.Direct3Subtitle",
                      _activeAgentEntryTarget ?? _activeChannelSelectorButton)
            };

            FrameworkElement? resolvedAnchor = anchor is { IsLoaded: true } ? anchor : null;
            tip.Title = _loc.T(titleKey);
            tip.Subtitle = _loc.T(subKey);
            tip.PreferredPlacement = ResolveOptimalPlacement(resolvedAnchor);
            tip.Target = resolvedAnchor;
            tip.ActionButtonContent = _loc.T(_activeStepTourStep < 3
                ? (_activeStepTourStep == 1 ? "CustomWidget.Workshop.AgentMcp.TourNext1" : "CustomWidget.Workshop.AgentMcp.TourNext2")
                : "CustomWidget.Workshop.Onboarding.TourFinish");
            tip.CloseButtonContent = _loc.T(_activeStepTourStep > 1
                ? "CustomWidget.Workshop.Onboarding.TourPrevStep"
                : "CustomWidget.Workshop.AgentMcp.TourSkip");
        }

        void RenderStep(int step)
        {
            if (tip.IsOpen)
            {
                // Close and reopen on the next UI tick so WinUI 3 recalculates tail direction
                // and root-bound clamping cleanly when switching to a target near the window bottom.
                isTransitioningStep = true;
                tip.IsOpen = false;
                ApplyStepContent(step);
                _shell.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_shell.IsClosed || runId != _onboardingTourRunId || !ReferenceEquals(_activeTourTeachingTip, tip))
                    {
                        return;
                    }

                    tip.IsOpen = true;
                    isTransitioningStep = false;
                });
                return;
            }

            ApplyStepContent(step);
        }

        tip.ActionButtonClick += (_, _) =>
        {
            if (_activeStepTourStep < 3)
            {
                RenderStep(_activeStepTourStep + 1);
            }
            else
            {
                FinishTour();
            }
        };
        tip.CloseButtonClick += (_, _) =>
        {
            if (_activeStepTourStep > 1)
            {
                RenderStep(_activeStepTourStep - 1);
            }
            else
            {
                FinishTour();
            }
        };
        tip.Closed += (_, args) =>
        {
            if (!isTransitioningStep && args.Reason == TeachingTipCloseReason.LightDismiss)
            {
                FinishTour();
            }
        };

        ApplyStepContent(initialStep);
        tip.IsOpen = true;
        return Task.CompletedTask;
    }

    internal async Task ShowAgentMcpQuickConnectDialogAsync(Action? onAfterChange = null)
    {
        if (_shell.IsDialogOpen || _shell.IsClosed || _shell.ShellRootGrid.XamlRoot is null)
        {
            return;
        }

        _shell.IsDialogOpen = true;
        using var bridgeDiagCts = new CancellationTokenSource();
        try
        {
            ElementTheme theme = StudioUiPrimitives.GetStudioExplicitTheme(_shell);
            var rootStack = new StackPanel { Spacing = 12, Width = 476 };
            rootStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.AgentMcp.QuickConnectIntro"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush")
            });

            var feedbackBar = new InfoBar { IsClosable = true, IsOpen = false };
            rootStack.Children.Add(feedbackBar);

            var panelHost = new StackPanel { Spacing = 10 };
            McpQuickConnectPanelBuilder.PopulatePanel(
                panelHost,
                theme,
                k => _loc.T(k),
                (k, a) => _loc.Format(k, a),
                _loc.CurrentCultureName,
                _quickConnectFormat,
                fmt => _quickConnectFormat = fmt,
                includeAutoSetupPromptCard: true,
                onSelfTestBanner: (ok, msg) =>
                {
                    feedbackBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                    feedbackBar.Message = msg;
                    feedbackBar.IsOpen = true;
                    onAfterChange?.Invoke();
                },
                bridgeDiagCts.Token);
            rootStack.Children.Add(panelHost);

            var dialog = new ContentDialog
            {
                XamlRoot = _shell.ShellRootGrid.XamlRoot,
                RequestedTheme = theme,
                FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(_loc.CurrentCultureName)
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight,
                Title = _loc.T("CustomWidget.Workshop.AgentMcp.QuickConnectTitle"),
                Content = new ScrollViewer
                {
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Padding = new Thickness(0, 0, 4, 0),
                    Content = rootStack
                },
                SecondaryButtonText = _loc.T("CustomWidget.AgentHub.OpenSetupDocsButton"),
                CloseButtonText = _loc.T("Common.Close"),
                DefaultButton = ContentDialogButton.None
            };
            if (Application.Current?.Resources is { } res &&
                res.TryGetValue("DefaultContentDialogStyle", out object? style) &&
                style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }

            ContentDialogResult dlgResult = await dialog.ShowAsync();
            if (dlgResult == ContentDialogResult.Secondary &&
                Uri.TryCreate(CustomWidgetAgentConnectorService.PublicGuideUrl, UriKind.Absolute, out Uri? guideUri))
            {
                _ = await Windows.System.Launcher.LaunchUriAsync(guideUri);
            }
        }
        finally
        {
            bridgeDiagCts.Cancel();
            _shell.IsDialogOpen = false;
        }
    }

    public void Dispose()
    {
        CloseOnboardingOverlay();
        ResetUiDelegates();
    }
}
