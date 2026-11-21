using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.Studio;

internal sealed class StudioComposerCardBuilder
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private CustomWidgetStudioSessionStore _sessionStore => _shell.SessionStore;
    private StudioSessionController _session => _shell.Session;

    internal StudioComposerCardBuilder(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }

    private readonly List<Action> _refreshActiveComposerCapabilityCallbacks = [];
    private readonly List<Action> _refreshApiDocsToggleCallbacks = [];
    private TextBox? _startIdeaTextBox;
    private Button? _activeCapabilityPickerButton;
    private Button? _activeComposerSubmitButton;
    private readonly List<Action> _refreshStartEmojiBadgeCallbacks = [];
    private readonly List<Action> _refreshComposerAsyncStatusCallbacks = [];
    private readonly List<Action> _refreshSendModeCallbacks = [];

    internal TextBox? StartIdeaTextBox { get => _startIdeaTextBox; set => _startIdeaTextBox = value; }
    internal Button? ActiveCapabilityPickerButton { get => _activeCapabilityPickerButton; set => _activeCapabilityPickerButton = value; }
    internal Button? ActiveComposerSubmitButton { get => _activeComposerSubmitButton; set => _activeComposerSubmitButton = value; }

    private static void InvokeLifecycleCallbacks(List<Action> callbacks)
    {
        foreach (Action cb in callbacks.ToArray())
        {
            cb();
        }
    }

    internal void RefreshActiveComposerCapabilityChrome() =>
        InvokeLifecycleCallbacks(_refreshActiveComposerCapabilityCallbacks);

    internal void RefreshApiDocsToggleButtons() =>
        InvokeLifecycleCallbacks(_refreshApiDocsToggleCallbacks);

    internal void RefreshStartEmojiBadge() =>
        InvokeLifecycleCallbacks(_refreshStartEmojiBadgeCallbacks);

    internal void RefreshComposerAsyncStatus() =>
        InvokeLifecycleCallbacks(_refreshComposerAsyncStatusCallbacks);

    internal void RefreshSendModeButtons() =>
        InvokeLifecycleCallbacks(_refreshSendModeCallbacks);

    internal void ClearPageLifecycleCallbacks()
    {
        _refreshActiveComposerCapabilityCallbacks.Clear();
        _refreshApiDocsToggleCallbacks.Clear();
        _refreshStartEmojiBadgeCallbacks.Clear();
        _refreshComposerAsyncStatusCallbacks.Clear();
        _refreshSendModeCallbacks.Clear();
        _flyoutInitialAtQuery.Clear();
        _flyoutQuerySyncCallbacks.Clear();
    }

    private static void BindLifecycleCallback(FrameworkElement element, List<Action> list, Action callback)
    {
        if (!list.Contains(callback))
        {
            list.Add(callback);
        }
        element.Loaded += (_, _) =>
        {
            if (!list.Contains(callback))
            {
                list.Add(callback);
            }
        };
        element.Unloaded += (_, _) => list.Remove(callback);
    }

    internal Border CreateComposerCard(
        string initialText,
        string placeholderText,
        string automationName,
        string submitLabel,
        string shortcutHintText,
        double minTextHeight,
        double maxTextHeight,
        bool compactToolbar,
        bool includeCodeToolInMore,
        Action<string>? onTextEdited,
        out TextBox textBox)
        => CreateComposerCard(
            initialText,
            placeholderText,
            automationName,
            submitLabel,
            shortcutHintText,
            minTextHeight,
            maxTextHeight,
            compactToolbar,
            includeCodeToolInMore,
            onTextEdited,
            out textBox,
            out _);

    internal Border CreateComposerCard(
        string initialText,
        string placeholderText,
        string automationName,
        string submitLabel,
        string shortcutHintText,
        double minTextHeight,
        double maxTextHeight,
        bool compactToolbar,
        bool includeCodeToolInMore,
        Action<string>? onTextEdited,
        out TextBox textBox,
        out Button submitButtonOut)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "CardBackgroundFillColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var pinnedChipsWrap = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 6,
            VerticalSpacing = 6
        };
        var pinnedChipsHost = new Border
        {
            Padding = new Thickness(12, 10, 12, 0),
            Visibility = Visibility.Collapsed,
            Child = pinnedChipsWrap
        };
        root.Children.Add(pinnedChipsHost);

        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = minTextHeight,
            MaxHeight = maxTextHeight,
            // 800 会把用户从别处粘来的详细需求静默截断（截断处没有提示、没有计数）。
            // 需求描述本身就是长文本，这里放宽到 4000，Shift+Enter 仍可换行。
            MaxLength = 4000,
            FontSize = 14,
            Padding = compactToolbar ? new Thickness(13, 10, 13, 5) : new Thickness(14, 12, 14, 6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            PlaceholderText = placeholderText,
            Text = initialText
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        StudioUiPrimitives.ApplyBorderlessTextBoxResources(box);
        AutomationProperties.SetName(box, automationName);

        bool isBoxFocused = false;
        box.GotFocus += (_, _) =>
        {
            isBoxFocused = true;
            card.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "AccentFillColorDefaultBrush");
        };
        box.LostFocus += (_, _) =>
        {
            isBoxFocused = false;
            card.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush");
        };
        card.PointerEntered += (_, _) =>
        {
            if (!isBoxFocused)
            {
                card.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "ControlElevationBorderBrush");
            }
        };
        card.PointerExited += (_, _) =>
        {
            if (!isBoxFocused)
            {
                card.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush");
            }
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
            {
                return;
            }

            if (e.Key == VirtualKey.Escape &&
                _session.Page == StudioPage.Preview &&
                (_session.IsInspectorModeEnabled || _session.SelectedTargetNode is not null))
            {
                _session.IsInspectorModeEnabled = false;
                _session.SelectedTargetNode = null;
                if (_shell.StageViewport.PreviewContent is not null)
                {
                    _shell.StageViewport.PreviewContent.SelectedNodePath = null;
                    _shell.StageViewport.PreviewContent.IsNodeSelectionEnabled = false;
                }

                _shell.StageViewport.RefreshStageToolbarChrome();
                _shell.ChatPanel.RefreshTargetNodePillChrome();
                e.Handled = true;
                return;
            }

            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            CoreVirtualKeyStates shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            CoreVirtualKeyStates ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            CoreVirtualKeyStates altState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
            bool shiftDown = (shiftState & CoreVirtualKeyStates.Down) != 0;
            bool ctrlDown = (ctrlState & CoreVirtualKeyStates.Down) != 0;
            bool altDown = (altState & CoreVirtualKeyStates.Down) != 0;

            bool shouldSubmit = _prefs.SendOnEnter
                ? (!shiftDown && !altDown)
                : (ctrlDown && !shiftDown && !altDown);
            if (!shouldSubmit)
            {
                return;
            }

            e.Handled = true;
            if ((_session.IsInPlaceGenerating && _session.WaitingKind == WaitingKind.Direct) || _session.IsI18nQueueRunning)
            {
                return;
            }

            string promptToSubmit = box.Text;
            _ = StudioUiPrimitives.SafeInvokeAsync(
                _shell,
                "ComposerEnterSubmit",
                () => _shell.Generation.BeginGenerationAsync(promptToSubmit));
        };

        Grid.SetRow(box, 1);
        root.Children.Add(box);

        double toolbarButtonHeight = compactToolbar ? 28 : 32;

        var footer = new Grid
        {
            Padding = compactToolbar ? new Thickness(10, 3, 10, 8) : new Thickness(10, 4, 10, 9),
            ColumnSpacing = 8
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        Action? refreshPasteVisibility = null;
        DropDownButton channelSelector = _shell.ChannelOnboarding.CreateAiChannelSelectorButton(() => refreshPasteVisibility?.Invoke());
        channelSelector.Height = toolbarButtonHeight;
        channelSelector.MaxWidth = 260;
        _shell.ChannelOnboarding.ActiveChannelSelectorButton = channelSelector;
        leftBar.Children.Add(channelSelector);

        if (!compactToolbar)
        {
            DropDownButton emojiPickerBtn = CreateStartEmojiPickerButton();
            leftBar.Children.Add(emojiPickerBtn);
        }

        var capabilityPickerBtn = new Button
        {
            Height = toolbarButtonHeight,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        ToolTipService.SetToolTip(capabilityPickerBtn, _loc.T("CustomWidget.CapabilityPicker.Tooltip"));
        AutomationProperties.SetName(capabilityPickerBtn, _loc.T("CustomWidget.CapabilityPicker.Button"));

        Flyout capabilityFlyout = BuildCapabilityPickerFlyout(box);
        capabilityPickerBtn.Flyout = capabilityFlyout;
        _activeCapabilityPickerButton = capabilityPickerBtn;
        leftBar.Children.Add(capabilityPickerBtn);

        void RefreshComposerCapabilityChrome()
        {
            string culture = _loc.CurrentCultureName;
            int pinCount = _session.PinnedCapabilityIds.Count;
            string btnText = pinCount > 0
                ? $"{_loc.T("CustomWidget.CapabilityPicker.ButtonShort")} · {pinCount}"
                : _loc.T(compactToolbar ? "CustomWidget.CapabilityPicker.ButtonShort" : "CustomWidget.CapabilityPicker.Button");
            capabilityPickerBtn.Content = StudioUiPrimitives.CreateSmallIconLabel("\uE74C", btnText);
            capabilityPickerBtn.BorderBrush = pinCount > 0
                ? StudioUiPrimitives.ThemeBrush(_shell, "AccentFillColorDefaultBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            pinnedChipsWrap.Children.Clear();
            if (pinCount == 0)
            {
                pinnedChipsHost.Visibility = Visibility.Collapsed;
                return;
            }

            pinnedChipsHost.Visibility = Visibility.Visible;
            foreach (string rawId in _session.PinnedCapabilityIds.ToList())
            {
                CustomWidgetCapabilitySpec? spec = CustomWidgetCapabilityRegistry.GetById(rawId);
                if (spec is null)
                {
                    continue;
                }

                string capturedId = spec.Id;
                string shortCapTitle = StudioManifestInspector.StripTrailingCapabilitySuffix(spec.GetTitle(culture));
                var pillRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var inspectBtn = new Button
                {
                    Height = 24,
                    Padding = new Thickness(6, 0, 4, 0),
                    MinWidth = 0,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    FontSize = 11.5,
                    Content = StudioUiPrimitives.CreateSmallIconLabel(spec.Glyph, shortCapTitle)
                };
                ToolTipService.SetToolTip(inspectBtn, $"@{spec.Id} — {spec.GetSummary(culture)}");
                inspectBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
                    _shell,
                    "ComposerPinnedCapabilityInspect",
                    () => _shell.ShowApiDocsDialogInStudioAsync(capturedId));
                pillRow.Children.Add(inspectBtn);

                var unpinBtn = new Button
                {
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 9 }
                };
                unpinBtn.Click += (_, _) => _shell.TogglePinnedCapability(capturedId);
                pillRow.Children.Add(unpinBtn);

                pinnedChipsWrap.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(2, 0, 3, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
                    Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
                    Child = pillRow
                });
            }
        }

        BindLifecycleCallback(card, _refreshActiveComposerCapabilityCallbacks, RefreshComposerCapabilityChrome);
        RefreshComposerCapabilityChrome();

        bool hasConfiguredDirectApi = DeskBoxAiService.Shared.HasAnyConfiguredProvider();
        var quickSetupApiButton = new Button
        {
            Height = toolbarButtonHeight,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE945", _loc.T("CustomWidget.Workshop.Composer.QuickSetupApi")),
            Visibility = hasConfiguredDirectApi ? Visibility.Collapsed : Visibility.Visible
        };
        ToolTipService.SetToolTip(quickSetupApiButton, _loc.T("CustomWidget.Workshop.Composer.ConfigKey"));
        AutomationProperties.SetName(quickSetupApiButton, _loc.T("CustomWidget.Workshop.Composer.QuickSetupApi"));
        quickSetupApiButton.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "ComposerQuickSetup", async () =>
        {
            if (_session.ResolveEffectiveStudioChannel(out _) == StudioAiChannelKind.AgentMcp)
            {
                await _shell.ChannelOnboarding.ShowAgentMcpQuickConnectDialogAsync(_shell.ChannelOnboarding.NotifyChannelSelectorsRefresh);
            }
            else
            {
                await _shell.ChannelOnboarding.ShowAiConfigDialogInStudioAsync(null, _shell.ChannelOnboarding.NotifyChannelSelectorsRefresh);
            }
        });
        _shell.ChannelOnboarding.ActiveQuickSetupButton = quickSetupApiButton;
        leftBar.Children.Add(quickSetupApiButton);

        var clearButton = new Button
        {
            Height = 28,
            Padding = new Thickness(8, 2, 8, 3),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            Content = _loc.T("CustomWidget.Workshop.Composer.Clear"),
            Visibility = string.IsNullOrWhiteSpace(initialText) ? Visibility.Collapsed : Visibility.Visible
        };
        clearButton.Click += (_, _) =>
        {
            box.Text = string.Empty;
            if (!compactToolbar)
            {
                _session.SelectedEmoji = null;
                RefreshStartEmojiBadge();
            }

            onTextEdited?.Invoke(string.Empty);
            box.Focus(FocusState.Programmatic);
        };
        leftBar.Children.Add(clearButton);

        if (compactToolbar)
        {
            var asyncStatusText = new TextBlock
            {
                FontSize = 11.5,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            leftBar.Children.Add(asyncStatusText);

            void RefreshAsyncComposerStatus()
            {
                if (_session.IsInPlaceGenerating && _session.WaitingKind == WaitingKind.AgentMcp)
                {
                    asyncStatusText.Text = DeskBoxAgentBridgeServer.LastMcpClient is { } mcpClient
                        ? _loc.Format("CustomWidget.Workshop.Composer.McpLastClientFormat", mcpClient.ClientName)
                        : _loc.T("CustomWidget.Workshop.Composer.McpBridgeReady");
                    asyncStatusText.Visibility = Visibility.Visible;
                }
                else if (_session.IsI18nQueueRunning && !_session.IsLocaleCenterOpen)
                {
                    asyncStatusText.Text = _loc.T("CustomWidget.Workshop.I18n.CenterTitle") + "…";
                    asyncStatusText.Visibility = Visibility.Visible;
                }
                else
                {
                    asyncStatusText.Visibility = Visibility.Collapsed;
                }
            }

            BindLifecycleCallback(card, _refreshComposerAsyncStatusCallbacks, RefreshAsyncComposerStatus);
            RefreshAsyncComposerStatus();
        }

        footer.Children.Add(leftBar);

        var rightBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (!compactToolbar)
        {
            Button moreButton = StudioUiPrimitives.CreateIconButton("\uE712", _loc.T("CustomWidget.Workshop.More.Tooltip"));
            moreButton.Width = 32;
            moreButton.Height = 32;
            moreButton.CornerRadius = new CornerRadius(6);
            moreButton.Flyout = BuildComposerMoreFlyout(() => box.Text);
            rightBar.Children.Add(moreButton);
        }

        string ResolveActiveShortcutHint() => _prefs.SendOnEnter ? "Enter ↵" : "Ctrl+↵";

        var sendModeToggleButton = new Button
        {
            Height = compactToolbar ? 30 : 32,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush")
        };
        rightBar.Children.Add(sendModeToggleButton);

        string initialShortcutHint = ResolveActiveShortcutHint();
        string submitTooltip = string.IsNullOrWhiteSpace(initialShortcutHint)
            ? submitLabel
            : $"{submitLabel} ({initialShortcutHint})";

        Button submitButton;
        if (compactToolbar)
        {
            submitButton = new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6)
            };
        }
        else
        {
            submitButton = new Button
            {
                Height = 32,
                Padding = new Thickness(16, 0, 16, 0),
                CornerRadius = new CornerRadius(6),
                Style = StudioUiPrimitives.AccentButtonStyle(),
                Content = StudioUiPrimitives.CreateSmallIconLabel("\uE945", submitLabel)
            };
        }

        bool isShowingStopChrome = false;
        long lastStopClickTickMs = 0;
        ToolTipService.SetToolTip(submitButton, submitTooltip);
        AutomationProperties.SetName(submitButton, submitLabel);
        submitButton.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "ComposerSubmitClick", async () =>
        {
            if ((_session.IsInPlaceGenerating || isShowingStopChrome) && compactToolbar)
            {
                lastStopClickTickMs = Environment.TickCount64;
                isShowingStopChrome = false;
                _shell.CancelWaiting();
                RefreshCompactSubmitChrome(box.Text ?? string.Empty);
                return;
            }

            // Guard against rapid double-click on Stop (including after prompt text is restored into box)
            if (lastStopClickTickMs > 0 && Environment.TickCount64 - lastStopClickTickMs < 450)
            {
                return;
            }

            await _shell.Generation.BeginGenerationAsync(box.Text);
        });
        _activeComposerSubmitButton = submitButton;
        rightBar.Children.Add(submitButton);

        void RefreshCompactSubmitChrome(string currentText)
        {
            if (!compactToolbar)
            {
                return;
            }

            if (_session.IsInPlaceGenerating)
            {
                isShowingStopChrome = true;
                string stopLabel = _loc.T("CustomWidget.Workshop.Stream.StopButton");
                submitButton.Style = null;
                submitButton.Background = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCriticalBackgroundBrush");
                submitButton.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCriticalBrush");
                submitButton.BorderThickness = new Thickness(1);
                submitButton.Foreground = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCriticalBrush");
                submitButton.Content = StudioUiPrimitives.CreateSolidStopSquareIcon(_shell);
                ToolTipService.SetToolTip(submitButton, stopLabel);
                AutomationProperties.SetName(submitButton, stopLabel);
                return;
            }

            isShowingStopChrome = false;

            bool isAgentMcp = _session.ResolveEffectiveStudioChannel(out _) == StudioAiChannelKind.AgentMcp;
            bool hasText = !string.IsNullOrWhiteSpace(currentText);
            if (hasText)
            {
                submitButton.Style = StudioUiPrimitives.AccentButtonStyle();
                submitButton.ClearValue(Control.BackgroundProperty);
                submitButton.ClearValue(Control.ForegroundProperty);
                submitButton.ClearValue(Control.BorderBrushProperty);
                submitButton.ClearValue(Control.BorderThicknessProperty);
            }
            else
            {
                submitButton.Style = null;
                submitButton.Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush");
                submitButton.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                submitButton.BorderThickness = new Thickness(0);
                submitButton.Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush");
            }

            string effectiveLabel = isAgentMcp
                ? _loc.T("CustomWidget.Workshop.Composer.SubmitAgentMcp")
                : submitLabel;
            string activeHint = ResolveActiveShortcutHint();
            string effectiveTip = string.IsNullOrWhiteSpace(activeHint)
                ? effectiveLabel
                : $"{effectiveLabel} ({activeHint})";
            submitButton.Content = new FontIcon { Glyph = isAgentMcp ? "\uE8C8" : "\uE74A", FontSize = 13 };
            ToolTipService.SetToolTip(submitButton, effectiveTip);
            AutomationProperties.SetName(submitButton, effectiveLabel);
        }

        void RefreshSendModeChrome()
        {
            bool enterSends = _prefs.SendOnEnter;
            string shortLabel = ResolveActiveShortcutHint();
            string tip = _loc.T(enterSends
                ? "CustomWidget.Workshop.Composer.SendModeEnterTip"
                : "CustomWidget.Workshop.Composer.SendModeCtrlEnterTip");
            sendModeToggleButton.Content = new TextBlock
            {
                Text = shortLabel,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(sendModeToggleButton, tip);
            AutomationProperties.SetName(sendModeToggleButton, tip);
            if (!compactToolbar)
            {
                bool isAgentMcpChannel = _session.ResolveEffectiveStudioChannel(out _) == StudioAiChannelKind.AgentMcp;
                string currentSubmitLabel = isAgentMcpChannel
                    ? _loc.T("CustomWidget.Workshop.Composer.SubmitAgentMcp")
                    : submitLabel;
                ToolTipService.SetToolTip(submitButton, $"{currentSubmitLabel} ({shortLabel})");
            }
            else
            {
                RefreshCompactSubmitChrome(box.Text ?? string.Empty);
            }
        }

        sendModeToggleButton.Click += (_, _) =>
        {
            _prefs.SetSendOnEnter(!_prefs.SendOnEnter);
            RefreshSendModeButtons();
        };
        BindLifecycleCallback(card, _refreshSendModeCallbacks, RefreshSendModeChrome);
        RefreshSendModeChrome();

        if (compactToolbar)
        {
            BindLifecycleCallback(
                card,
                _refreshComposerAsyncStatusCallbacks,
                () => RefreshCompactSubmitChrome(box.Text ?? string.Empty));
        }

        double latestCardWidth = 760;
        refreshPasteVisibility = () =>
        {
            StudioAiChannelKind channel = _session.ResolveEffectiveStudioChannel(out string? activeDirectId);
            bool isDirectChannel = channel == StudioAiChannelKind.DirectApi;
            bool isAgentMcpChannel = channel == StudioAiChannelKind.AgentMcp;

            _shell.ChannelOnboarding.ActiveAgentEntryTarget = channelSelector;

            if (isAgentMcpChannel)
            {
                string mcpSetupText = _loc.T("CustomWidget.Workshop.Composer.QuickSetupMcp");
                quickSetupApiButton.Content = compactToolbar && latestCardWidth < 460
                    ? new FontIcon { Glyph = "\uE943", FontSize = 12 }
                    : StudioUiPrimitives.CreateSmallIconLabel("\uE943", mcpSetupText);
                quickSetupApiButton.Padding = compactToolbar && latestCardWidth < 460
                    ? new Thickness(8, 0, 8, 0)
                    : new Thickness(10, 0, 10, 0);
                quickSetupApiButton.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(quickSetupApiButton, _loc.T("CustomWidget.Workshop.Composer.QuickSetupMcpTooltip"));
                AutomationProperties.SetName(quickSetupApiButton, mcpSetupText);
            }
            else if (isDirectChannel)
            {
                bool hasDirectConfigured = DeskBoxAiService.Shared.HasAnyConfiguredProvider();
                string apiSetupText = _loc.T("CustomWidget.Workshop.Composer.QuickSetupApi");
                quickSetupApiButton.Content = StudioUiPrimitives.CreateSmallIconLabel("\uE945", apiSetupText);
                quickSetupApiButton.Padding = new Thickness(10, 0, 10, 0);
                quickSetupApiButton.Visibility = hasDirectConfigured ? Visibility.Collapsed : Visibility.Visible;
                ToolTipService.SetToolTip(quickSetupApiButton, _loc.T("CustomWidget.Workshop.Composer.ConfigKey"));
                AutomationProperties.SetName(quickSetupApiButton, apiSetupText);
            }

            if (!compactToolbar)
            {
                string currentSubmitLabel = isAgentMcpChannel
                    ? _loc.T("CustomWidget.Workshop.Composer.SubmitAgentMcp")
                    : submitLabel;
                string activeHint = ResolveActiveShortcutHint();
                string currentSubmitTip = string.IsNullOrWhiteSpace(activeHint)
                    ? currentSubmitLabel
                    : $"{currentSubmitLabel} ({activeHint})";
                submitButton.Content = StudioUiPrimitives.CreateSmallIconLabel(isAgentMcpChannel ? "\uE8C8" : "\uE945", currentSubmitLabel);
                ToolTipService.SetToolTip(submitButton, currentSubmitTip);
                AutomationProperties.SetName(submitButton, currentSubmitLabel);
            }
            else
            {
                RefreshCompactSubmitChrome(box.Text ?? string.Empty);
            }
        };

        RefreshCompactSubmitChrome(initialText ?? string.Empty);
        refreshPasteVisibility();

        string prevBoxText = initialText ?? string.Empty;
        box.TextChanged += (_, _) =>
        {
            string current = box.Text ?? string.Empty;
            clearButton.Visibility = string.IsNullOrWhiteSpace(current)
                ? Visibility.Collapsed
                : Visibility.Visible;
            RefreshCompactSubmitChrome(current);
            onTextEdited?.Invoke(current);

            // Power-user '@' trigger: typing '@<query>' at start or after whitespace opens & filters Capability Picker
            if (TryExtractActiveAtQuery(current, box.SelectionStart, out _, out string atQuery))
            {
                if (current.Length >= prevBoxText.Length && !capabilityFlyout.IsOpen)
                {
                    SetAttachedAtInitialQuery(capabilityFlyout, atQuery);
                    FlyoutBase.ShowAttachedFlyout(capabilityPickerBtn);
                    capabilityFlyout.ShowAt(capabilityPickerBtn);
                }
                else if (capabilityFlyout.IsOpen)
                {
                    UpdateAttachedAtLiveQuery(capabilityFlyout, atQuery);
                }
            }
            prevBoxText = current;
        };

        Grid.SetColumn(rightBar, 1);
        footer.Children.Add(rightBar);

        card.SizeChanged += (_, e) =>
        {
            latestCardWidth = e.NewSize.Width;
            refreshPasteVisibility();
            if (!compactToolbar)
            {
                channelSelector.MaxWidth = Math.Clamp(latestCardWidth - 340, 140, 260);
            }
            else
            {
                channelSelector.MaxWidth = Math.Clamp(latestCardWidth - 230, 128, 228);
            }
        };

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        card.Child = root;
        textBox = box;
        submitButtonOut = submitButton;
        return card;
    }

    private DropDownButton CreateStartEmojiPickerButton()
    {
        var emojiText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btn = new DropDownButton
        {
            Height = 32,
            Padding = new Thickness(9, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = emojiText
        };
        string tip = _loc.T("CustomWidget.Workshop.Composer.EmojiPickerTooltip");
        ToolTipService.SetToolTip(btn, tip);
        AutomationProperties.SetName(btn, tip);

        void RefreshBadge()
        {
            bool hasExplicit = CustomWidgetTemplates.IsEmojiIcon(_session.SelectedEmoji);
            emojiText.Text = hasExplicit ? _session.SelectedEmoji!.Trim() : "\u2728";
            btn.BorderBrush = hasExplicit
                ? StudioUiPrimitives.ThemeBrush(_shell, "AccentFillColorDefaultBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        BindLifecycleCallback(btn, _refreshStartEmojiBadgeCallbacks, RefreshBadge);
        RefreshBadge();

        btn.Flyout = EmojiPickerFlyoutHelper.BuildEmojiPickerFlyout(
            resolveTheme: () => StudioUiPrimitives.GetStudioExplicitTheme(_shell),
            translate: _loc.T,
            currentEmoji: _session.SelectedEmoji,
            allowAutoMode: true,
            onPickedAsync: picked =>
            {
                _session.SelectedEmoji = string.IsNullOrWhiteSpace(picked) || picked == "✨" ? null : picked;
                RefreshBadge();
                return Task.CompletedTask;
            },
            resolveLiveEmoji: () => _session.SelectedEmoji,
            placement: FlyoutPlacementMode.BottomEdgeAlignedLeft);
        return btn;
    }

    private MenuFlyout BuildComposerMoreFlyout(Func<string> readText)
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            StudioAiChannelKind activeChannel = _session.ResolveEffectiveStudioChannel(out _);

            if (activeChannel == StudioAiChannelKind.AgentMcp)
            {
                _shell.AddMenuItem(flyout, "\uE756", "CustomWidget.Workshop.More.CopyAgentTask",
                    async () => await _shell.CommitAndWorkspace.CopyAgentKickoffPromptAsync(readText()));
                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            _shell.AddMenuItem(flyout, "\uE8A1", "CustomWidget.ApiDocs.Button",
                async () => await _shell.ShowApiDocsDialogInStudioAsync());
        };
        return flyout;
    }

    private Flyout BuildCapabilityPickerFlyout(TextBox composerBox)
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.TopEdgeAlignedLeft
        };

        var root = new Grid
        {
            Width = 368,
            RowSpacing = 8
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(310) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var searchBox = new AutoSuggestBox
        {
            PlaceholderText = _loc.T("CustomWidget.CapabilityPicker.SearchPlaceholder"),
            QueryIcon = new SymbolIcon(Symbol.Find),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        root.Children.Add(searchBox);

        var listStack = new StackPanel { Spacing = 3, Padding = new Thickness(0, 0, 4, 2) };
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listStack
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        var bottomBar = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush")
        };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var selectableButtons = new List<Button>();

        var clearPinsBtn = new Button
        {
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Content = _loc.T("CustomWidget.Workshop.Composer.Clear")
        };
        clearPinsBtn.Click += (_, _) =>
        {
            _shell.ClearPinnedCapabilities();
            RenderItems();
        };
        bottomBar.Children.Add(clearPinsBtn);

        var openDocsBtn = new Button
        {
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE8A1", _loc.T("CustomWidget.CapabilityPicker.OpenApiDocs"))
        };
        openDocsBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "CapabilityFlyoutOpenDocs", async () =>
        {
            flyout.Hide();
            await _shell.ShowApiDocsDialogInStudioAsync();
        });
        Grid.SetColumn(openDocsBtn, 2);
        bottomBar.Children.Add(openDocsBtn);

        Grid.SetRow(bottomBar, 2);
        root.Children.Add(bottomBar);
        int keyboardSelectedIndex = -1;

        void HighlightSelectedButton()
        {
            for (int idx = 0; idx < selectableButtons.Count; idx++)
            {
                Button b = selectableButtons[idx];
                bool isSel = idx == keyboardSelectedIndex;
                b.BorderThickness = new Thickness(isSel ? 1.5 : (b.Tag is true ? 1 : 0));
                if (isSel)
                {
                    b.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "AccentFillColorDefaultBrush");
                    b.StartBringIntoView();
                }
            }
        }

        void StripActiveAtQueryInComposer()
        {
            string cur = composerBox.Text ?? string.Empty;
            if (TryExtractActiveAtQuery(cur, composerBox.SelectionStart, out int atIndex, out string activeQuery))
            {
                int removeLen = 1 + activeQuery.Length;
                composerBox.Text = cur.Remove(atIndex, removeLen);
                composerBox.SelectionStart = Math.Clamp(atIndex, 0, composerBox.Text.Length);
            }
            else if (!string.IsNullOrEmpty(cur) && cur.EndsWith('@'))
            {
                composerBox.Text = cur[..^1];
                composerBox.SelectionStart = composerBox.Text.Length;
            }
        }

        void RenderItems()
        {
            listStack.Children.Clear();
            selectableButtons.Clear();
            keyboardSelectedIndex = -1;
            string culture = _loc.CurrentCultureName;
            string q = searchBox.Text ?? string.Empty;
            clearPinsBtn.IsEnabled = _session.PinnedCapabilityIds.Count > 0;

            if (string.IsNullOrWhiteSpace(q) &&
                _session.Page == StudioPage.Preview &&
                !_session.IsCapsulePreviewMode &&
                !string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
            {
                var pickRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8
                };
                pickRow.Children.Add(new FontIcon
                {
                    Glyph = "\uE81D",
                    FontSize = 13.5,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                pickRow.Children.Add(new TextBlock
                {
                    Text = _loc.T("CustomWidget.Workshop.CoCreate.AtPickElementAction"),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var pickElementActionBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 0, 0, 4),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
                    Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
                    Content = pickRow
                };
                pickElementActionBtn.Click += (_, _) =>
                {
                    flyout.Hide();
                    StripActiveAtQueryInComposer();

                    _session.IsInspectorModeEnabled = true;
                    if (_shell.StageViewport.PreviewContent is not null)
                    {
                        _shell.StageViewport.PreviewContent.IsNodeSelectionEnabled = true;
                    }

                    _shell.StageViewport.RefreshStageToolbarChrome();
                };
                selectableButtons.Add(pickElementActionBtn);
                listStack.Children.Add(pickElementActionBtn);
            }

            IReadOnlyList<CustomWidgetCapabilitySpec> matches = CustomWidgetCapabilityRegistry.Search(q);
            if (matches.Count == 0)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = _loc.T("CustomWidget.ApiDocs.EmptySearch"),
                    FontSize = 12,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
                    Margin = new Thickness(6, 12, 6, 12)
                });
                return;
            }

            CustomWidgetCapabilityCategory[] catOrder =
            [
                CustomWidgetCapabilityCategory.DataSource,
                CustomWidgetCapabilityCategory.Component,
                CustomWidgetCapabilityCategory.Action,
                CustomWidgetCapabilityCategory.ConfigAndSecurity
            ];

            foreach (CustomWidgetCapabilityCategory cat in catOrder)
            {
                List<CustomWidgetCapabilitySpec> group = matches.Where(c => c.Category == cat).ToList();
                if (group.Count == 0)
                {
                    continue;
                }

                listStack.Children.Add(new TextBlock
                {
                    Text = CustomWidgetCapabilityRegistry.GetCategoryDisplayName(cat, culture),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
                    Margin = new Thickness(4, 6, 4, 2)
                });

                foreach (CustomWidgetCapabilitySpec spec in group)
                {
                    string capturedId = spec.Id;
                    bool pinned = _session.IsCapabilityPinned(spec.Id);

                    var rowGrid = new Grid { ColumnSpacing = 10 };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    rowGrid.Children.Add(new FontIcon
                    {
                        Glyph = spec.Glyph,
                        FontSize = 14,
                        Foreground = pinned
                            ? StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush")
                            : StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    var textCol = new StackPanel { Spacing = 1 };
                    var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    titleLine.Children.Add(new TextBlock
                    {
                        Text = StudioManifestInspector.StripTrailingCapabilitySuffix(spec.GetTitle(culture)),
                        FontSize = 12.5,
                        FontWeight = pinned ? FontWeights.SemiBold : FontWeights.Normal
                    });
                    titleLine.Children.Add(new TextBlock
                    {
                        Text = "@" + spec.Id,
                        FontSize = 10.5,
                        FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    textCol.Children.Add(titleLine);

                    textCol.Children.Add(new TextBlock
                    {
                        Text = spec.GetSummary(culture),
                        FontSize = 11,
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    Grid.SetColumn(textCol, 1);
                    rowGrid.Children.Add(textCol);

                    var checkIcon = new FontIcon
                    {
                        Glyph = pinned ? "\uE73E" : "\uE710",
                        FontSize = 12,
                        Foreground = pinned
                            ? StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush")
                            : StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(checkIcon, 2);
                    rowGrid.Children.Add(checkIcon);

                    var itemBtn = new Button
                    {
                        Tag = pinned,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(8, 6, 8, 6),
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(pinned ? 1 : 0),
                        BorderBrush = pinned
                            ? StudioUiPrimitives.ThemeBrush(_shell, "AccentFillColorDefaultBrush")
                            : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Background = pinned
                            ? StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush")
                            : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Content = rowGrid
                    };
                    itemBtn.Click += (_, _) =>
                    {
                        StripActiveAtQueryInComposer();
                        _shell.TogglePinnedCapability(capturedId);
                        RenderItems();
                    };
                    selectableButtons.Add(itemBtn);
                    listStack.Children.Add(itemBtn);
                }
            }

            if (selectableButtons.Count > 0)
            {
                keyboardSelectedIndex = 0;
                HighlightSelectedButton();
            }
        }

        StudioUiPrimitives.BindFlyoutToStudioTheme(_shell, flyout, root);
        searchBox.TextChanged += (_, _) => RenderItems();
        searchBox.PreviewKeyDown += (_, e) =>
        {
            if (selectableButtons.Count == 0)
            {
                return;
            }

            if (e.Key == VirtualKey.Down)
            {
                e.Handled = true;
                keyboardSelectedIndex = (keyboardSelectedIndex + 1) % selectableButtons.Count;
                HighlightSelectedButton();
            }
            else if (e.Key == VirtualKey.Up)
            {
                e.Handled = true;
                keyboardSelectedIndex = (keyboardSelectedIndex - 1 + selectableButtons.Count) % selectableButtons.Count;
                HighlightSelectedButton();
            }
            else if (e.Key == VirtualKey.Enter && keyboardSelectedIndex >= 0 && keyboardSelectedIndex < selectableButtons.Count)
            {
                e.Handled = true;
                Button chosen = selectableButtons[keyboardSelectedIndex];
                if (Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(chosen) is
                    Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer peer)
                {
                    peer.Invoke();
                }
                flyout.Hide();
                composerBox.Focus(FocusState.Programmatic);
            }
        };
        RegisterCapabilityFlyoutQuerySync(flyout, q =>
        {
            if (!string.Equals(searchBox.Text, q, StringComparison.Ordinal))
            {
                searchBox.Text = q;
            }
        });
        flyout.Opening += (_, _) =>
        {
            searchBox.Text = ConsumeAttachedAtInitialQuery(flyout);
            RenderItems();
        };
        flyout.Opened += (_, _) =>
        {
            _shell.DispatcherQueue.TryEnqueue(() => searchBox.Focus(FocusState.Programmatic));
        };

        flyout.Content = root;
        return flyout;
    }

    private readonly ConditionalWeakTable<Flyout, StrongBox<string>> _flyoutInitialAtQuery = new();
    private readonly ConditionalWeakTable<Flyout, Action<string>> _flyoutQuerySyncCallbacks = new();

    private void SetAttachedAtInitialQuery(Flyout flyout, string query) =>
        _flyoutInitialAtQuery.AddOrUpdate(flyout, new StrongBox<string>(query ?? string.Empty));

    private string ConsumeAttachedAtInitialQuery(Flyout flyout)
    {
        if (_flyoutInitialAtQuery.TryGetValue(flyout, out StrongBox<string>? box))
        {
            _flyoutInitialAtQuery.Remove(flyout);
            return box.Value ?? string.Empty;
        }
        return string.Empty;
    }

    private void RegisterCapabilityFlyoutQuerySync(Flyout flyout, Action<string> callback) =>
        _flyoutQuerySyncCallbacks.AddOrUpdate(flyout, callback);

    private void UpdateAttachedAtLiveQuery(Flyout flyout, string query)
    {
        if (_flyoutQuerySyncCallbacks.TryGetValue(flyout, out Action<string>? cb))
        {
            cb(query ?? string.Empty);
        }
    }

    internal static bool TryExtractActiveAtQuery(string text, int caretPos, out int atIndex, out string query)
    {
        atIndex = -1;
        query = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        int effectiveCaret = Math.Clamp(caretPos > 0 ? caretPos : text.Length, 0, text.Length);
        if (effectiveCaret == 0)
        {
            return false;
        }

        int scan = effectiveCaret - 1;
        while (scan >= 0 && !char.IsWhiteSpace(text[scan]) && text[scan] != '@')
        {
            if (effectiveCaret - scan > 28)
            {
                return false;
            }
            scan--;
        }

        if (scan < 0 || text[scan] != '@')
        {
            return false;
        }

        bool afterBoundary = scan == 0 || char.IsWhiteSpace(text[scan - 1]);
        if (!afterBoundary)
        {
            return false;
        }

        atIndex = scan;
        query = text[(scan + 1)..effectiveCaret];
        return true;
    }

    internal Button CreateSubtleApiDocsToggleButton(
        string labelKey,
        double fontSize,
        double horizontalPadding)
    {
        var icon = new FontIcon
        {
            Glyph = "\uE8A1",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = _loc.T(labelKey),
            FontSize = fontSize,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var contentRow = new Grid { ColumnSpacing = 6 };
        contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentRow.Children.Add(icon);
        Grid.SetColumn(label, 1);
        contentRow.Children.Add(label);

        var button = new Button
        {
            Height = 32,
            Padding = new Thickness(horizontalPadding, 0, horizontalPadding, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Content = contentRow
        };
        ToolTipService.SetToolTip(button, _loc.T("CustomWidget.ApiDocs.Tooltip"));
        AutomationProperties.SetName(button, _loc.T("CustomWidget.ApiDocs.Button"));

        void ApplyVisual()
        {
            bool open = _shell.IsApiDocsDrawerOpen;
            button.Background = StudioUiPrimitives.ThemeBrush(_shell, open ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush");
            button.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, open ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
            icon.Foreground = StudioUiPrimitives.ThemeBrush(_shell, open ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            label.Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorPrimaryBrush");
        }

        BindLifecycleCallback(button, _refreshApiDocsToggleCallbacks, ApplyVisual);
        ApplyVisual();
        button.ActualThemeChanged += (_, _) => ApplyVisual();
        button.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "ToggleApiDocsDrawer", async () =>
        {
            await _shell.ShowApiDocsDialogInStudioAsync();
            ApplyVisual();
        });

        return button;
    }

    internal InfoBar CreatePreviewIncomingAgentStageBanner(
        Action<string> onDismissedStageId,
        out Action<AgentStageSummary, int> showAction,
        out Action hideAction)
    {
        var bar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            IsClosable = false,
            IsOpen = true,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var loadBtn = new Button
        {
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Style = StudioUiPrimitives.AccentButtonStyle(),
            Content = _loc.T("CustomWidget.Workshop.AgentStage.ViewButton")
        };
        var dismissBtn = new Button
        {
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Content = _loc.T("CustomWidget.Workshop.AgentStage.DiscardButton")
        };

        AgentStageSummary? currentStage = null;
        loadBtn.Click += (_, _) =>
        {
            if (currentStage is { } stage)
            {
                bar.Visibility = Visibility.Collapsed;
                _shell.LoadAgentStageIntoPreview(stage, forceLoadOverUnsaved: true);
            }
        };
        dismissBtn.Click += (_, _) =>
        {
            if (currentStage is { } stage)
            {
                onDismissedStageId(stage.StageId);
            }
            bar.Visibility = Visibility.Collapsed;
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 2)
        };
        actions.Children.Add(loadBtn);
        actions.Children.Add(dismissBtn);
        bar.Content = actions;

        showAction = (stage, pendingCount) =>
        {
            currentStage = stage;
            bar.Message = _loc.Format("CustomWidget.Workshop.AgentStage.Notice", Math.Max(1, pendingCount));
            bar.Visibility = Visibility.Visible;
        };
        hideAction = () =>
        {
            currentStage = null;
            bar.Visibility = Visibility.Collapsed;
        };
        return bar;
    }

    /// <summary>
    /// P2：首屏「来自外部工具的待确认更改」提示条。
    /// 批次来自 App.GetPendingAgentStages()；「查看预览」只读取暂存内容，
    /// 「忽略」直接丢弃批次，两者都不会改动桌面。
    /// </summary>
    internal InfoBar? CreateAgentStageBannerIfNeeded()
    {
        var bar = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            IsOpen = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var viewBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12.5,
            Content = _loc.T("CustomWidget.Workshop.AgentStage.ViewButton")
        };
        var dismissBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12.5,
            Content = _loc.T("CustomWidget.Workshop.AgentStage.DiscardButton")
        };

        string? currentStageId = null;

        void Refresh()
        {
            var pending = _hostBridge.GetPendingAgentStages();
            currentStageId = pending.Count > 0 ? pending[0].StageId : null;
            bar.Visibility = currentStageId is null ? Visibility.Collapsed : Visibility.Visible;
            if (currentStageId is not null)
            {
                bar.Message = _loc.Format("CustomWidget.Workshop.AgentStage.Notice", pending.Count);
            }

            viewBtn.IsEnabled = currentStageId is not null;
            dismissBtn.IsEnabled = currentStageId is not null;
        }

        viewBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(currentStageId))
            {
                _shell.OpenAgentStagePreview(currentStageId);
            }
        };

        dismissBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "DiscardAgentStage", async () =>
        {
            string? id = currentStageId;
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            dismissBtn.IsEnabled = false;
            try
            {
                await _hostBridge.DiscardAgentStageAsync(id);
            }
            finally
            {
                dismissBtn.IsEnabled = true;
                Refresh();
            }
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 2)
        };
        actions.Children.Add(viewBtn);
        actions.Children.Add(dismissBtn);
        bar.Content = actions;

        _shell.StartGallery.RefreshAgentStageBanner = Refresh;
        Refresh();
        return bar;
    }

    internal InfoBar? CreateDraftRecoveryBannerIfNeeded()
    {
        if (_session.PendingRecoveryDraft is not { } draft)
        {
            return null;
        }

        var continueBtn = new Button
        {
            Content = _loc.T("CustomWidget.Workshop.CoCreate.DraftContinue"),
            CornerRadius = new CornerRadius(6)
        };
        continueBtn.Click += (_, _) => _shell.RestoreFromDraftSnapshot(draft);

        var bar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            IsClosable = true,
            IsOpen = true,
            Message = _loc.T("CustomWidget.Workshop.CoCreate.DraftRecoveryMessage"),
            ActionButton = continueBtn
        };
        bar.CloseButtonClick += (_, _) =>
        {
            _session.DismissPendingRecoveryDraft();
            bar.Visibility = Visibility.Collapsed;
        };
        return bar;
    }

    internal void ResetUiDelegates()
    {
        ClearPageLifecycleCallbacks();
        _startIdeaTextBox = null;
        _activeCapabilityPickerButton = null;
        _activeComposerSubmitButton = null;
    }
}
