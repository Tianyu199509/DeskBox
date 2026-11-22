using System.Diagnostics;
using System.Globalization;
using System.IO;
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

internal sealed class StudioCoCreationChatPanel : IDisposable
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private CustomWidgetStudioSessionStore _sessionStore => _shell.SessionStore;
    private StudioSessionController _session => _shell.Session;

    internal StudioCoCreationChatPanel(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }
    private int _lastRenderedChatMessageCount;
    private Button? _undoButton;
    private Button? _redoButton;
    private DropDownButton? _revisionsMenuButton;
    private Button? _briefHeaderButton;
    private ScrollViewer? _chatStreamScroller;
    private StackPanel? _chatStreamPanel;
    private Border? _stageHealthDiagnosticBar;
    private CustomWidgetWrapPanel? _suggestionChipsPanel;
    private Border? _targetNodePillBorder;
    private TextBlock? _targetNodePillText;
    private TextBox? _refineTextBox;
    private Button? _refineSubmitButton;
    private Button? _applyInPlaceButton;
    private Button? _commitButton;
    private TextBlock? _directStageTitleText;
    private TextBlock? _directStatsBadgeText;
    private Border? _directThinkingContainer;
    private TextBlock? _directThinkingHeaderText;
    private TextBlock? _directThinkingCollapsedPreviewText;
    private ScrollViewer? _directThinkingScroller;
    private TextBlock? _directThinkingText;
    private StackPanel? _directActionTracePanel;
    private readonly List<ReconciledTraceEntry<(Grid Row, TextBlock LabelText)>> _renderedTraceRows = [];
    private TextBlock? _directStreamHintText;
    private Border? _directStep1Pill;
    private Border? _directStep2Pill;
    private Border? _directStep3Pill;
    private string _directActiveProviderName = string.Empty;
    private string _directActiveModelName = string.Empty;
    private long _directStartTickMs;
    private int _directLastCharTotal;
    private int _directLastTokenTotal;
    private bool _directThinkingCollapsedByUser = true;
    private IReadOnlyList<StudioStreamStepItem> _inFlightTraceSteps = [];
    private readonly List<string> _mcpActivityItems = [];
    private bool _chatStickToBottom = true;
    private bool _thinkingStickToBottom = true;
    private int _programmaticChatScrollDepth;
    private int _programmaticThinkingScrollDepth;
    private bool _pendingChatStickyScrollQueued;
    private bool _pendingThinkingStickyScrollQueued;
    private StackPanel? _retainedStreamHostPanel;
    private readonly Dictionary<string, (string StateKey, UIElement Element)> _retainedStreamItems = new(StringComparer.Ordinal);

    internal int LastRenderedChatMessageCount { get => _lastRenderedChatMessageCount; set => _lastRenderedChatMessageCount = value; }
    internal Button? UndoButton { get => _undoButton; set => _undoButton = value; }
    internal Button? RedoButton { get => _redoButton; set => _redoButton = value; }
    internal DropDownButton? RevisionsMenuButton { get => _revisionsMenuButton; set => _revisionsMenuButton = value; }
    internal Button? BriefHeaderButton { get => _briefHeaderButton; set => _briefHeaderButton = value; }
    internal ScrollViewer? ChatStreamScroller { get => _chatStreamScroller; set => _chatStreamScroller = value; }
    internal StackPanel? ChatStreamPanel { get => _chatStreamPanel; set => _chatStreamPanel = value; }
    internal Border? StageHealthDiagnosticBar { get => _stageHealthDiagnosticBar; set => _stageHealthDiagnosticBar = value; }
    internal CustomWidgetWrapPanel? SuggestionChipsPanel { get => _suggestionChipsPanel; set => _suggestionChipsPanel = value; }
    internal Border? TargetNodePillBorder { get => _targetNodePillBorder; set => _targetNodePillBorder = value; }
    internal TextBlock? TargetNodePillText { get => _targetNodePillText; set => _targetNodePillText = value; }
    internal TextBox? RefineTextBox { get => _refineTextBox; set => _refineTextBox = value; }
    internal Button? RefineSubmitButton { get => _refineSubmitButton; set => _refineSubmitButton = value; }
    internal Button? ApplyInPlaceButton { get => _applyInPlaceButton; set => _applyInPlaceButton = value; }
    internal Button? CommitButton { get => _commitButton; set => _commitButton = value; }
    internal TextBlock? DirectStageTitleText { get => _directStageTitleText; set => _directStageTitleText = value; }
    internal TextBlock? DirectStatsBadgeText { get => _directStatsBadgeText; set => _directStatsBadgeText = value; }
    internal Border? DirectThinkingContainer { get => _directThinkingContainer; set => _directThinkingContainer = value; }
    internal TextBlock? DirectThinkingHeaderText { get => _directThinkingHeaderText; set => _directThinkingHeaderText = value; }
    internal TextBlock? DirectThinkingCollapsedPreviewText { get => _directThinkingCollapsedPreviewText; set => _directThinkingCollapsedPreviewText = value; }
    internal ScrollViewer? DirectThinkingScroller { get => _directThinkingScroller; set => _directThinkingScroller = value; }
    internal TextBlock? DirectThinkingText { get => _directThinkingText; set => _directThinkingText = value; }
    internal StackPanel? DirectActionTracePanel { get => _directActionTracePanel; set => _directActionTracePanel = value; }
    internal TextBlock? DirectStreamHintText { get => _directStreamHintText; set => _directStreamHintText = value; }
    internal Border? DirectStep1Pill { get => _directStep1Pill; set => _directStep1Pill = value; }
    internal Border? DirectStep2Pill { get => _directStep2Pill; set => _directStep2Pill = value; }
    internal Border? DirectStep3Pill { get => _directStep3Pill; set => _directStep3Pill = value; }
    internal string DirectActiveProviderName { get => _directActiveProviderName; set => _directActiveProviderName = value; }
    internal string DirectActiveModelName { get => _directActiveModelName; set => _directActiveModelName = value; }
    internal long DirectStartTickMs { get => _directStartTickMs; set => _directStartTickMs = value; }
    internal int DirectLastCharTotal { get => _directLastCharTotal; set => _directLastCharTotal = value; }
    internal int DirectLastTokenTotal { get => _directLastTokenTotal; set => _directLastTokenTotal = value; }
    internal bool DirectThinkingCollapsedByUser { get => _directThinkingCollapsedByUser; set => _directThinkingCollapsedByUser = value; }
    internal IReadOnlyList<StudioStreamStepItem> InFlightTraceSteps { get => _inFlightTraceSteps; set => _inFlightTraceSteps = value; }
    internal bool ChatStickToBottom { get => _chatStickToBottom; set => _chatStickToBottom = value; }
    internal bool ThinkingStickToBottom { get => _thinkingStickToBottom; set => _thinkingStickToBottom = value; }
    internal bool PendingChatStickyScrollQueued { get => _pendingChatStickyScrollQueued; set => _pendingChatStickyScrollQueued = value; }
    internal bool PendingThinkingStickyScrollQueued { get => _pendingThinkingStickyScrollQueued; set => _pendingThinkingStickyScrollQueued = value; }

    internal void RefreshInPlaceGenerationChrome(bool refreshStream = true)
    {
        if (_shell.StageViewport.StageGeneratingProgressBar is not null)
        {
            _shell.StageViewport.StageGeneratingProgressBar.IsIndeterminate = _session.IsInPlaceGenerating;
            if (_shell.StageViewport.StageGeneratingProgressRing is not null)
            {
                _shell.StageViewport.StageGeneratingProgressRing.IsActive = _session.IsInPlaceGenerating;
            }
            _shell.StageViewport.StageGeneratingProgressBar.Visibility = _session.IsInPlaceGenerating ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_shell.StageViewport.StageGeneratingStatusPill is not null && _shell.StageViewport.StageGeneratingStatusText is not null)
        {
            if (_session.IsInPlaceGenerating)
            {
                _shell.StageViewport.StageGeneratingStatusText.Text = _session.WaitingKind == WaitingKind.AgentMcp
                    ? _loc.T("CustomWidget.Workshop.Stage.StatusWaitingAgent")
                    : _loc.T("CustomWidget.Workshop.Stage.StatusUpdating");
                _shell.StageViewport.StageGeneratingStatusPill.Visibility = Visibility.Visible;
            }
            else
            {
                _shell.StageViewport.StageGeneratingStatusPill.Visibility = Visibility.Collapsed;
            }
        }

        _shell.ComposerBuilder.RefreshComposerAsyncStatus();

        if (_refineTextBox is not null)
        {
            _refineTextBox.IsEnabled = !_session.IsI18nQueueRunning;
        }

        if (_commitButton is not null)
        {
            _commitButton.IsEnabled = !_session.IsAnyStudioJobRunning && !string.IsNullOrWhiteSpace(_session.CurrentManifestJson);
        }

        if (_applyInPlaceButton is not null)
        {
            _applyInPlaceButton.IsEnabled = !_session.IsAnyStudioJobRunning && !string.IsNullOrWhiteSpace(_session.CurrentManifestJson);
        }

        if (_undoButton is not null)
        {
            _undoButton.IsEnabled = !_session.IsAnyStudioJobRunning && _session.RevisionIndex > 0;
        }

        if (_redoButton is not null)
        {
            _redoButton.IsEnabled = !_session.IsAnyStudioJobRunning && _session.RevisionIndex < _session.Revisions.Count - 1;
        }

        RefreshTargetNodePillChrome();
        _shell.StageViewport.RefreshScriptErrorBannerChrome();
        _shell.LocaleCenter.RefreshLocaleCenterChrome();
        RefreshSuggestionChips();

        if (refreshStream)
        {
            RefreshCoCreationStream();
        }
    }

    internal FrameworkElement BuildRefinePanel()
    {
        var layout = new Grid { Margin = new Thickness(8, 0, 0, 0), RowSpacing = 10 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0 — Docked Multi-Locale Progress & Live Preview Center
        _shell.LocaleCenter.LocaleCenterHostBorder = _shell.LocaleCenter.BuildLocaleCenterPanel();
        layout.Children.Add(_shell.LocaleCenter.LocaleCenterHostBorder);

        // Row 1 — Persistent Multi-Turn Co-Creation Stream (sits directly on Mica workspace layer)
        _chatStreamPanel = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(0, 4, 8, 14)
        };
        _chatStreamScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _chatStreamPanel
        };
        _chatStreamScroller.ViewChanged += (s, _) =>
        {
            if (_programmaticChatScrollDepth > 0 || s is not ScrollViewer sv)
            {
                return;
            }

            _chatStickToBottom = (sv.ScrollableHeight - sv.VerticalOffset) <= 24.0;
        };
        _chatStreamScroller.PointerWheelChanged += (s, e) =>
        {
            if (s is not ScrollViewer sv)
            {
                return;
            }

            int delta = e.GetCurrentPoint(sv).Properties.MouseWheelDelta;
            if (delta > 0 && sv.ScrollableHeight > 0.5)
            {
                _chatStickToBottom = false;
            }
        };
        Grid.SetRow(_chatStreamScroller, 1);
        layout.Children.Add(_chatStreamScroller);

        // Row 1 — Bottom Dock: InfoBar + Picked Node Pill + Curated Chips + Compact-Submit Multi-Line Composer
        var dockStack = new StackPanel { Spacing = 8 };

        _shell.PageInfoBar = _shell.CreatePageInfoBar();
        dockStack.Children.Add(_shell.PageInfoBar);

        _shell.StageViewport.ScriptErrorBannerText = new TextBlock
        {
            FontSize = 12,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCriticalBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        var copyScriptErrBtn = StudioUiPrimitives.CreateIconButton("\uE8C8", _loc.T("Common.Copy"));
        copyScriptErrBtn.Width = 26;
        copyScriptErrBtn.Height = 26;
        copyScriptErrBtn.CornerRadius = new CornerRadius(5);
        copyScriptErrBtn.Click += (_, _) =>
        {
            string errText = _shell.StageViewport.ScriptErrorBannerText?.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(errText))
            {
                _shell.SetClipboardText(errText);
            }
        };

        _shell.StageViewport.ScriptErrorFixButton = new Button
        {
            Height = 26,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(5),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE945", _loc.T("CustomWidget.Workshop.ScriptError.FixWithAi"))
        };
        _shell.StageViewport.ScriptErrorFixButton.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "ScriptErrorBannerAutoFix",
            _shell.StageViewport.TriggerRuntimeScriptAutoFixAsync);

        var scriptErrorActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        scriptErrorActions.Children.Add(copyScriptErrBtn);
        scriptErrorActions.Children.Add(_shell.StageViewport.ScriptErrorFixButton);

        var scriptErrorGrid = new Grid { ColumnSpacing = 8 };
        scriptErrorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scriptErrorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scriptErrorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scriptErrorGrid.Children.Add(new FontIcon
        {
            Glyph = "\uE7BA",
            FontSize = 13,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCriticalBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_shell.StageViewport.ScriptErrorBannerText, 1);
        scriptErrorGrid.Children.Add(_shell.StageViewport.ScriptErrorBannerText);
        Grid.SetColumn(scriptErrorActions, 2);
        scriptErrorGrid.Children.Add(scriptErrorActions);

        _shell.StageViewport.ScriptErrorBannerBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 8, 6),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCriticalBackgroundBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
            Child = scriptErrorGrid
        };
        dockStack.Children.Add(_shell.StageViewport.ScriptErrorBannerBorder);

        _targetNodePillText = new TextBlock
        {
            FontSize = 12,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        var clearPickBtn = new Button
        {
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }
        };
        ToolTipService.SetToolTip(clearPickBtn, _loc.T("CustomWidget.Workshop.CoCreate.ClearPick"));
        AutomationProperties.SetName(clearPickBtn, _loc.T("CustomWidget.Workshop.CoCreate.ClearPick"));
        clearPickBtn.Click += (_, _) =>
        {
            _session.SelectedTargetNode = null;
            if (_shell.StageViewport.PreviewContent is not null)
            {
                _shell.StageViewport.PreviewContent.SelectedNodePath = null;
            }
            RefreshTargetNodePillChrome();
        };

        var targetPillGrid = new Grid { ColumnSpacing = 6 };
        targetPillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetPillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetPillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetPillGrid.Children.Add(new FontIcon
        {
            Glyph = "\uE81D",
            FontSize = 12,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_targetNodePillText, 1);
        targetPillGrid.Children.Add(_targetNodePillText);
        Grid.SetColumn(clearPickBtn, 2);
        targetPillGrid.Children.Add(clearPickBtn);

        _targetNodePillBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
            Child = targetPillGrid
        };
        dockStack.Children.Add(_targetNodePillBorder);

        _stageHealthDiagnosticBar = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            Visibility = Visibility.Collapsed
        };
        dockStack.Children.Add(_stageHealthDiagnosticBar);

        _suggestionChipsPanel = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        dockStack.Children.Add(_suggestionChipsPanel);

        Border refineComposer = _shell.ComposerBuilder.CreateComposerCard(
            initialText: _session.RefineDraftIdea,
            placeholderText: _loc.T("CustomWidget.Workshop.Refine.Placeholder"),
            automationName: _loc.T("CustomWidget.Workshop.Refine.Header"),
            submitLabel: _loc.T("CustomWidget.Workshop.Refine.Button"),
            shortcutHintText: _loc.T("CustomWidget.Workshop.Composer.RefineHint"),
            minTextHeight: 46,
            maxTextHeight: 164,
            compactToolbar: true,
            includeCodeToolInMore: true,
            onTextEdited: text => _session.RefineDraftIdea = text.Trim(),
            out TextBox refineBox,
            out Button refineSubmitBtn);
        _refineTextBox = refineBox;
        _refineSubmitButton = refineSubmitBtn;
        dockStack.Children.Add(refineComposer);

        Grid.SetRow(dockStack, 2);
        layout.Children.Add(dockStack);

        return layout;
    }

    internal void RefreshBriefButtonChrome()
    {
        if (_briefHeaderButton is null)
        {
            return;
        }

        bool hasBrief = !string.IsNullOrWhiteSpace(_session.BriefMarkdown);
        string label = _loc.T(hasBrief
            ? "CustomWidget.Workshop.CoCreate.BriefButtonActive"
            : "CustomWidget.Workshop.CoCreate.BriefButton");
        _briefHeaderButton.Content = StudioUiPrimitives.CreateSmallIconLabel(hasBrief ? "\uE90A" : "\uE7C3", label);
        ToolTipService.SetToolTip(_briefHeaderButton, _loc.T("CustomWidget.Workshop.CoCreate.BriefDialogDesc"));
        AutomationProperties.SetName(_briefHeaderButton, label);
    }

    internal void RefreshTargetNodePillChrome()
    {
        if (_targetNodePillBorder is null || _targetNodePillText is null)
        {
            return;
        }

        if (_session.IsInPlaceGenerating || _session.IsCapsulePreviewMode || _session.SelectedTargetNode is not { } picked)
        {
            _targetNodePillBorder.Visibility = Visibility.Collapsed;
            _shell.StageInspector.RefreshStageInspectorBar();
            return;
        }

        string nodeTag = CustomWidgetNativeRenderer.FormatPickedNodeTag(picked.NodeType, picked.DisplayLabel, _loc);
        string baseText = _loc.Format("CustomWidget.Workshop.CoCreate.PickedPillSingle", nodeTag);
        string? scriptSummary = StudioManifestInspector.InspectNodeScriptSummary(_session.CurrentManifestJson, picked.NodePath);
        _targetNodePillText.Text = string.IsNullOrWhiteSpace(scriptSummary)
            ? baseText
            : $"{baseText} · {scriptSummary}";
        _targetNodePillBorder.Visibility = Visibility.Visible;
        _shell.StageInspector.RefreshStageInspectorBar();
    }

    internal void RefreshSuggestionChips()
    {
        if (_suggestionChipsPanel is null)
        {
            return;
        }

        if (_stageHealthDiagnosticBar is not null)
        {
            bool suppressHealthBar =
                _session.IsInPlaceGenerating ||
                _shell.StageViewport.IsBlockingPageDiagnosticOpen ||
                !string.IsNullOrWhiteSpace(_session.LiveRuntimeScriptError);

            CustomWidgetHealthIssue? topIssue = null;
            if (!suppressHealthBar)
            {
                CustomWidgetManifest? curManifest = _shell.StageViewport.ResolvePreviewManifestWithSidecar(_session.CurrentManifestJson);
                bool isUserFork = curManifest is not null &&
                    (curManifest.Id.StartsWith("user.fork-", StringComparison.OrdinalIgnoreCase) ||
                     !string.IsNullOrWhiteSpace(curManifest.ForkedFromId));
                topIssue = _session.LatestHealthReport is { HasIssues: true }
                    ? _session.LatestHealthReport.Issues.FirstOrDefault(i =>
                        !string.IsNullOrWhiteSpace(i.SuggestedPrompt) &&
                        !i.Code.StartsWith("layout.", StringComparison.OrdinalIgnoreCase) &&
                        !(isUserFork && string.Equals(i.Code, "i18n.incomplete", StringComparison.OrdinalIgnoreCase)))
                    : null;

                if (topIssue is null &&
                    !string.IsNullOrWhiteSpace(_session.CurrentManifestJson) &&
                    string.Equals(_session.LastBrokenJson, _session.CurrentManifestJson, StringComparison.Ordinal) &&
                    _session.LastDiagnostics.FirstOrDefault(static d => !d.IsError && !string.IsNullOrWhiteSpace(d.Message)) is { } firstWarn)
                {
                    topIssue = new CustomWidgetHealthIssue(
                        "validation.warning",
                        firstWarn.Message,
                        $"{firstWarn.Path}: {firstWarn.Message}",
                        IsWarning: true);
                }
            }

            if (topIssue is not null)
            {
                var healthGrid = new Grid { ColumnSpacing = 8 };
                healthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                healthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                healthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var warnIcon = new FontIcon
                {
                    Glyph = "\uE7BA",
                    FontSize = 13,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, topIssue.IsWarning
                        ? "SystemFillColorCautionBrush"
                        : "SystemFillColorCriticalBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                healthGrid.Children.Add(warnIcon);

                var msgText = new TextBlock
                {
                    Text = topIssue.Message,
                    FontSize = 12,
                    LineHeight = 16,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorPrimaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(msgText, topIssue.Message);
                Grid.SetColumn(msgText, 1);
                healthGrid.Children.Add(msgText);

                string issueCode = topIssue.Code;
                string fixDisplayPrompt = _loc.Format("CustomWidget.Workshop.CoCreate.AutoFixDisplayFormat", topIssue.Message);
                var autoFixBtn = new Button
                {
                    Content = _loc.T("CustomWidget.Workshop.Health.AutoFixBtn"),
                    Height = 26,
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(6),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                };
                autoFixBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
                    _shell,
                    "HealthDiagnosticAutoFix",
                    async () =>
                    {
                        if (string.Equals(issueCode, "i18n.incomplete", StringComparison.Ordinal))
                        {
                            string[]? existingLocaleCodes = _session.CurrentLocales?.Locales.Keys.ToArray();
                            await _shell.LocaleCenter.RunIncrementalI18nBatchAsync(
                                existingLocaleCodes is { Length: > 0 } ? existingLocaleCodes : null);
                            return;
                        }

                        string combinedFixPrompt = _shell.Generation.BuildCombinedHealthAndDiagnosticsRepairPrompt(topIssue);
                        await _shell.Generation.BeginGenerationAsync(combinedFixPrompt, displayPrompt: fixDisplayPrompt);
                    });
                Grid.SetColumn(autoFixBtn, 2);
                healthGrid.Children.Add(autoFixBtn);

                _stageHealthDiagnosticBar.Child = healthGrid;
                _stageHealthDiagnosticBar.Visibility = Visibility.Visible;
                _stageHealthDiagnosticBar.IsHitTestVisible = !_session.IsAnyStudioJobRunning;
                _stageHealthDiagnosticBar.Opacity = _session.IsAnyStudioJobRunning ? 0.55 : 1.0;
            }
            else
            {
                _stageHealthDiagnosticBar.Child = null;
                _stageHealthDiagnosticBar.Visibility = Visibility.Collapsed;
            }
        }

        if (_suggestionChipsPanel is null)
        {
            return;
        }

        _suggestionChipsPanel.Children.Clear();
        var addedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedPrompts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPromptChip(string chipLabel, string promptToFill, string? glyph = null, IconElement? iconElement = null)
        {
            if (_suggestionChipsPanel.Children.Count >= 4 ||
                string.IsNullOrWhiteSpace(chipLabel) ||
                string.IsNullOrWhiteSpace(promptToFill) ||
                !addedLabels.Add(chipLabel) ||
                !addedPrompts.Add(promptToFill.Trim()))
            {
                return;
            }

            Button chip = StudioUiPrimitives.CreateChipButton(chipLabel, glyph, iconElement);
            chip.Click += (_, _) =>
            {
                if (_refineTextBox is null)
                {
                    return;
                }

                string existing = (_refineTextBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(existing))
                {
                    _refineTextBox.Text = promptToFill;
                }
                else if (!existing.Contains(promptToFill, StringComparison.Ordinal))
                {
                    _refineTextBox.Text = $"{existing}\n{promptToFill}";
                }
                _refineTextBox.SelectionStart = (_refineTextBox.Text ?? string.Empty).Length;
                _refineTextBox.Focus(FocusState.Programmatic);
            };
            _suggestionChipsPanel.Children.Add(chip);
        }

        if (_session.IsCapsulePreviewMode)
        {
            AddPromptChip(
                _loc.T("CustomWidget.Workshop.Chip.CapsuleAction.Label"),
                _loc.T("CustomWidget.Workshop.Chip.CapsuleAction.Prompt"),
                "\uE710");
            AddPromptChip(
                _loc.T("CustomWidget.Workshop.Chip.CapsuleProgress.Label"),
                _loc.T("CustomWidget.Workshop.Chip.CapsuleProgress.Prompt"),
                glyph: null, iconElement: StudioUiPrimitives.CreateCapsuleViewportIcon());
        }

        StudioChatMessage? lastAssistant = _session.ChatMessages.LastOrDefault(static m =>
            string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
            !m.IsSuperseded);
        if (lastAssistant is { Suggestions.Count: > 0 } &&
            !string.Equals(lastAssistant.Kind, "clarify", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(lastAssistant.Kind, "answer", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string s in lastAssistant.Suggestions.Take(3))
            {
                string shortText = StudioManifestInspector.TruncateTextElements(s, 24);
                AddPromptChip(shortText, s, "\uE945");
            }
        }

        if (_session.ChatMessages.Count == 0)
        {
            (string Label, string Prompt)[] craftChips =
            [
                ("CustomWidget.Workshop.Chip.Softer.Label", "CustomWidget.Workshop.Chip.Softer.Prompt"),
                ("CustomWidget.Workshop.Chip.Bigger.Label", "CustomWidget.Workshop.Chip.Bigger.Prompt"),
                ("CustomWidget.Workshop.Chip.Progress.Label", "CustomWidget.Workshop.Chip.Progress.Prompt"),
                ("CustomWidget.Workshop.Chip.Compact.Label", "CustomWidget.Workshop.Chip.Compact.Prompt")
            ];
            foreach ((string labelKey, string promptKey) in craftChips)
            {
                AddPromptChip(_loc.T(labelKey), _loc.T(promptKey));
            }
        }

        _suggestionChipsPanel.Visibility = _suggestionChipsPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _suggestionChipsPanel.IsHitTestVisible = !_session.IsAnyStudioJobRunning;
        _suggestionChipsPanel.Opacity = _session.IsAnyStudioJobRunning ? 0.45 : 1.0;
    }

    internal void RefreshCoCreationStream()
    {
        if (_chatStreamPanel is null)
        {
            return;
        }

        if (!ReferenceEquals(_retainedStreamHostPanel, _chatStreamPanel))
        {
            _retainedStreamItems.Clear();
            _retainedStreamHostPanel = _chatStreamPanel;
        }

        _chatStreamPanel.Padding = new Thickness(0, 4, 8, 14);

        if (_session.ChatMessages.Count == 0 && !_session.IsInPlaceGenerating)
        {
            _retainedStreamItems.Clear();
            _lastRenderedChatMessageCount = 0;
            var emptyCard = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush"),
                BorderThickness = new Thickness(1),
                BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush")
            };
            var emptyStack = new StackPanel { Spacing = 6 };
            if (_session.RevisionIndex >= 0 && _session.RevisionIndex < _session.Revisions.Count)
            {
                emptyStack.Children.Add(new TextBlock
                {
                    Text = _loc.Format("CustomWidget.Workshop.History.Item", _session.RevisionIndex + 1, _session.Revisions[_session.RevisionIndex].ChangeSummary),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            emptyStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.CoCreate.EmptyStreamHint"),
                FontSize = 12.5,
                LineHeight = 19,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            emptyCard.Child = emptyStack;
            ReconcilePanelChildren(_chatStreamPanel, [emptyCard]);
            return;
        }

        var desiredChildren = new List<UIElement>();
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        string themeCultureSalt = $"{_loc.CurrentCultureName}:{StudioUiPrimitives.GetStudioExplicitTheme(_shell)}";

        int prevRenderedCount = _lastRenderedChatMessageCount;
        bool shouldAnimateNewBubbles = _session.ChatMessages.Count > prevRenderedCount &&
                                       (_session.ChatMessages.Count - prevRenderedCount) <= 3;

        var aiLinkedRevIndices = new HashSet<int>();
        foreach (StudioChatMessage chatMsg in _session.ChatMessages)
        {
            if (!string.IsNullOrWhiteSpace(chatMsg.LinkedRevisionId))
            {
                int idx = _session.Revisions.FindIndex(r =>
                    string.Equals(r.RevisionId, chatMsg.LinkedRevisionId, StringComparison.Ordinal));
                if (idx >= 0)
                {
                    aiLinkedRevIndices.Add(idx);
                }
            }
        }

        void AppendRetainedManualDivider(int revIdx, StudioPersistedRevision rev)
        {
            string divKey = $"div:{rev.RevisionId}:{revIdx}";
            string divState = $"{_session.RevisionIndex == revIdx}:{_session.IsAnyStudioJobRunning}:{rev.ChangeSummary}:{themeCultureSalt}";
            activeKeys.Add(divKey);
            if (_retainedStreamItems.TryGetValue(divKey, out var cachedDiv) &&
                string.Equals(cachedDiv.StateKey, divState, StringComparison.Ordinal))
            {
                desiredChildren.Add(cachedDiv.Element);
                return;
            }

            UIElement divider = CreateManualRevisionDivider(revIdx, rev);
            _retainedStreamItems[divKey] = (divState, divider);
            desiredChildren.Add(divider);
        }

        int nextManualRevCursor = 0;
        void FlushManualDividersUpTo(int? stopBeforeRevIndexExclusive, long? maxCreatedTicksInclusive)
        {
            while (nextManualRevCursor < _session.Revisions.Count)
            {
                if (stopBeforeRevIndexExclusive is int limitIdx && nextManualRevCursor >= limitIdx)
                {
                    break;
                }

                if (aiLinkedRevIndices.Contains(nextManualRevCursor))
                {
                    break;
                }

                StudioPersistedRevision rev = _session.Revisions[nextManualRevCursor];
                if (maxCreatedTicksInclusive is long maxTicks &&
                    rev.CreatedAtUtcTicks > 0 &&
                    maxTicks > 0 &&
                    rev.CreatedAtUtcTicks > maxTicks)
                {
                    break;
                }

                if (string.Equals(rev.Source, "manual", StringComparison.OrdinalIgnoreCase))
                {
                    AppendRetainedManualDivider(nextManualRevCursor, rev);
                }

                nextManualRevCursor++;
            }
        }

        for (int msgIndex = 0; msgIndex < _session.ChatMessages.Count; msgIndex++)
        {
            StudioChatMessage msg = _session.ChatMessages[msgIndex];
            bool isNewlyAdded = shouldAnimateNewBubbles && msgIndex >= prevRenderedCount;
            if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                FlushManualDividersUpTo(stopBeforeRevIndexExclusive: null, maxCreatedTicksInclusive: msg.CreatedAtUtcTicks);
                string uKey = $"u:{msgIndex}:{msg.CreatedAtUtcTicks}";
                string uState = $"{msg.Text}|{msg.TargetNodeLabel}|{msg.TargetNodePath}|{msg.IsSuperseded}|{themeCultureSalt}";
                activeKeys.Add(uKey);
                if (_retainedStreamItems.TryGetValue(uKey, out var cachedUser) &&
                    string.Equals(cachedUser.StateKey, uState, StringComparison.Ordinal))
                {
                    desiredChildren.Add(cachedUser.Element);
                    continue;
                }

                var userStack = new StackPanel { Spacing = 4 };
                string? targetLabel = !string.IsNullOrWhiteSpace(msg.TargetNodeLabel)
                    ? msg.TargetNodeLabel
                    : msg.TargetNodePath;
                if (!string.IsNullOrWhiteSpace(targetLabel))
                {
                    var targetRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5
                    };
                    targetRow.Children.Add(new FontIcon
                    {
                        Glyph = "\uE81D",
                        FontSize = 10.5,
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    targetRow.Children.Add(new TextBlock
                    {
                        Text = targetLabel,
                        FontSize = 11,
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    userStack.Children.Add(targetRow);
                }

                userStack.Children.Add(new TextBlock
                {
                    Text = msg.Text,
                    FontSize = 13,
                    LineHeight = 19,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });

                Border userBubble = StudioUiPrimitives.CreateUserMessageBubbleChrome(
                    _shell,
                    userStack,
                    opacity: msg.IsSuperseded ? 0.52 : 1.0);
                _retainedStreamItems[uKey] = (uState, userBubble);
                desiredChildren.Add(userBubble);
                if (isNewlyAdded)
                {
                    FluentMotionHelper.PlayEntranceSlideAndFade(userBubble, offsetY: 6f, durationMs: 165);
                }

                continue;
            }

            int linkedRevIndex = -1;
            if (!string.IsNullOrWhiteSpace(msg.LinkedRevisionId))
            {
                linkedRevIndex = _session.Revisions.FindIndex(r =>
                    string.Equals(r.RevisionId, msg.LinkedRevisionId, StringComparison.Ordinal));
            }
            if (linkedRevIndex >= 0)
            {
                FlushManualDividersUpTo(stopBeforeRevIndexExclusive: linkedRevIndex, maxCreatedTicksInclusive: null);
            }

            bool isFollowedByNewerTurn = msgIndex < _session.ChatMessages.Count - 1;
            string aKey = $"a:{msgIndex}:{msg.CreatedAtUtcTicks}:{msg.LinkedRevisionId}";
            string aState = $"{msg.Kind}|{msg.Text}|{msg.IsSuperseded}|{linkedRevIndex}|{linkedRevIndex == _session.RevisionIndex}|{isFollowedByNewerTurn}|{msg.SubmittedSummary}|{_session.IsAnyStudioJobRunning}|{msg.ExecutionSummary}|{msg.ExecutionSteps.Count}|{msg.Suggestions.Count}|{msg.ActionChips.Count}|{themeCultureSalt}";
            activeKeys.Add(aKey);
            if (_retainedStreamItems.TryGetValue(aKey, out var cachedAssistant) &&
                string.Equals(cachedAssistant.StateKey, aState, StringComparison.Ordinal))
            {
                desiredChildren.Add(cachedAssistant.Element);
                if (linkedRevIndex >= nextManualRevCursor)
                {
                    nextManualRevCursor = linkedRevIndex + 1;
                }
                continue;
            }

            bool isUnsupportedTurn = string.Equals(msg.Kind, "unsupported", StringComparison.OrdinalIgnoreCase);
            string badgeText = msg.Kind switch
            {
                "answer" => _loc.T("CustomWidget.Workshop.CoCreate.KindAnswer"),
                "clarify" => _loc.T("CustomWidget.Workshop.CoCreate.KindClarify"),
                "unsupported" => _loc.T("CustomWidget.Workshop.CoCreate.KindUnsupported"),
                _ => linkedRevIndex >= 0
                    ? _loc.Format("CustomWidget.Workshop.CoCreate.KindEdit", linkedRevIndex + 1)
                    : _loc.T("CustomWidget.Workshop.History.AiChange")
            };

            var cardStack = new StackPanel { Spacing = 8 };
            var metaRow = new Grid { ColumnSpacing = 8 };
            metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badgesRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgesRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Background = StudioUiPrimitives.ThemeBrush(
                    _shell,
                    isUnsupportedTurn ? "SystemFillColorCautionBackgroundBrush" : "SubtleFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badgeText,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = StudioUiPrimitives.ThemeBrush(
                        _shell,
                        isUnsupportedTurn ? "SystemFillColorCautionBrush" : "AccentTextFillColorPrimaryBrush")
                }
            });
            if (msg.IsSuperseded)
            {
                badgesRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = _loc.T("CustomWidget.Workshop.CoCreate.SupersededTag"),
                        FontSize = 11,
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush")
                    }
                });
            }
            metaRow.Children.Add(badgesRow);

            if (linkedRevIndex >= 0)
            {
                if (linkedRevIndex == _session.RevisionIndex)
                {
                    var curTag = new TextBlock
                    {
                        Text = _loc.T("CustomWidget.Workshop.CoCreate.CurrentRevTag"),
                        FontSize = 11.5,
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(curTag, 2);
                    metaRow.Children.Add(curTag);
                }
                else
                {
                    int targetRev = linkedRevIndex;
                    var restoreBtn = new Button
                    {
                        Height = 24,
                        Padding = new Thickness(8, 0, 8, 0),
                        CornerRadius = new CornerRadius(6),
                        FontSize = 11.5,
                        IsEnabled = !_session.IsAnyStudioJobRunning,
                        Content = _loc.T("CustomWidget.Workshop.CoCreate.RestoreRevBtn")
                    };
                    restoreBtn.Click += (_, _) => SelectRevision(targetRev);
                    Grid.SetColumn(restoreBtn, 2);
                    metaRow.Children.Add(restoreBtn);
                }
            }

            cardStack.Children.Add(metaRow);

            cardStack.Children.Add(CreateFormattedAssistantTextBlock(msg.Text));

            if (!string.IsNullOrWhiteSpace(msg.LimitationNote))
            {
                var limitGrid = new Grid { ColumnSpacing = 7 };
                limitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                limitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                limitGrid.Children.Add(new FontIcon
                {
                    Glyph = "\uE946",
                    FontSize = 12,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCautionBrush"),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                var limitText = new TextBlock
                {
                    Text = msg.LimitationNote,
                    FontSize = 11.5,
                    LineHeight = 16,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush")
                };
                Grid.SetColumn(limitText, 1);
                limitGrid.Children.Add(limitText);
                cardStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 6, 9, 6),
                    Background = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorCautionBackgroundBrush"),
                    BorderThickness = new Thickness(1),
                    BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush"),
                    Child = limitGrid
                });
            }

            if (msg.DiffPills.Count > 0 || linkedRevIndex >= 0)
            {
                var diffWrap = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 4 };
                foreach (string pill in msg.DiffPills)
                {
                    string capturedPill = pill;
                    var pillBtn = new Button
                    {
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(7, 2, 7, 2),
                        MinHeight = 0,
                        MinWidth = 0,
                        BorderThickness = new Thickness(0),
                        Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
                        Content = new TextBlock
                        {
                            Text = capturedPill,
                            FontSize = 11,
                            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush")
                        }
                    };
                    pillBtn.PointerEntered += (_, _) =>
                    {
                        if (_session.SelectedTargetNode is null &&
                            _shell.StageViewport.PreviewContent is { } pc &&
                            TryResolveNodePathFromDiffPill(capturedPill) is { } hoverPath)
                        {
                            pc.SelectedNodePath = hoverPath;
                        }
                    };
                    pillBtn.PointerExited += (_, _) =>
                    {
                        if (_session.SelectedTargetNode is null &&
                            _shell.StageViewport.PreviewContent is { } pc)
                        {
                            pc.SelectedNodePath = null;
                        }
                    };
                    pillBtn.Click += (_, _) =>
                    {
                        if (_shell.StageViewport.PreviewContent is { } pc &&
                            TryResolveNodePathFromDiffPill(capturedPill) is { } targetPath)
                        {
                            pc.SelectedNodePath = targetPath;
                        }
                        _ = _shell.PreviewTheme.PulseStageAsync();
                    };
                    diffWrap.Children.Add(pillBtn);
                }

                if (linkedRevIndex >= 0)
                {
                    var codeBtn = new Button
                    {
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(7, 2, 7, 2),
                        MinHeight = 0,
                        MinWidth = 0,
                        BorderThickness = new Thickness(0),
                        Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush"),
                        Content = StudioUiPrimitives.CreateSmallIconLabel("\uE943", _loc.T("CustomWidget.Workshop.More.ViewCode"))
                    };
                    codeBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
                        _shell,
                        "ChatDiffViewCode",
                        _shell.StageDialogs.ShowWorkspaceCodeViewerDialogAsync);
                    diffWrap.Children.Add(codeBtn);
                }

                if (diffWrap.Children.Count > 0)
                {
                    cardStack.Children.Add(diffWrap);
                }
            }

            bool hasTraceSteps = msg.ExecutionSteps is { Count: > 0 };
            bool hasPersistedThinking = !string.IsNullOrWhiteSpace(msg.ThinkingText);
            if (hasTraceSteps || hasPersistedThinking)
            {
                string traceTitle = !string.IsNullOrWhiteSpace(msg.ExecutionSummary)
                    ? msg.ExecutionSummary!
                    : (hasTraceSteps
                        ? _loc.Format("CustomWidget.Workshop.CoCreate.TraceStepsSummaryFormat", msg.ExecutionSteps.Count)
                        : _loc.Format(
                            "CustomWidget.Workshop.Stream.ThinkingDone",
                            StreamingTurnAccumulator.FormatCompactTokensWithUnit(
                                StreamingTurnAccumulator.EstimateTokenCount(msg.ThinkingText!))));
                var traceChevron = new FontIcon
                {
                    Glyph = "\uE70D",
                    FontSize = 10,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var traceRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                traceRow.Children.Add(traceChevron);
                traceRow.Children.Add(new TextBlock
                {
                    Text = traceTitle,
                    FontSize = 11.5,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var traceToggleBtn = new Button
                {
                    Padding = new Thickness(6, 2, 8, 2),
                    CornerRadius = new CornerRadius(5),
                    Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush"),
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Content = traceRow
                };
                var traceStepsStack = new StackPanel
                {
                    Spacing = 4,
                    Padding = new Thickness(8, 2, 0, 2),
                    Visibility = Visibility.Collapsed
                };
                if (hasPersistedThinking)
                {
                    string trimmedThinking = msg.ThinkingText!.Trim();
                    if (trimmedThinking.Length > 2400)
                    {
                        trimmedThinking = "…\n" + trimmedThinking[^2400..];
                    }

                    traceStepsStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(8, 6, 8, 6),
                        Margin = new Thickness(0, 0, 0, 2),
                        Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
                        Child = new TextBlock
                        {
                            Text = trimmedThinking,
                            FontSize = 11,
                            LineHeight = 16,
                            MaxLines = 18,
                            TextWrapping = TextWrapping.Wrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            IsTextSelectionEnabled = true,
                            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush")
                        }
                    });
                }

                if (hasTraceSteps)
                {
                    foreach (StudioStreamStepItem step in msg.ExecutionSteps)
                    {
                        string stepGlyph = step.State switch
                        {
                            StudioStreamStepState.Completed => "\uE73E",
                            _ => "\uE7BA"
                        };
                        string stepBrushKey = step.State switch
                        {
                            StudioStreamStepState.Completed => "SystemFillColorSuccessBrush",
                            _ => "SystemFillColorCautionBrush"
                        };
                        var stepRow = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6
                        };
                        stepRow.Children.Add(new FontIcon
                        {
                            Glyph = stepGlyph,
                            FontSize = 10.5,
                            Foreground = StudioUiPrimitives.ThemeBrush(_shell, stepBrushKey),
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        stepRow.Children.Add(new TextBlock
                        {
                            Text = step.Label,
                            FontSize = 11.5,
                            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        traceStepsStack.Children.Add(stepRow);
                    }
                }

                traceToggleBtn.Click += (_, _) =>
                {
                    bool expand = traceStepsStack.Visibility != Visibility.Visible;
                    traceStepsStack.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
                    traceChevron.Glyph = expand ? "\uE70E" : "\uE70D";
                };
                cardStack.Children.Add(traceToggleBtn);
                cardStack.Children.Add(traceStepsStack);
            }

            if (msg.ClarifyQuestions.Count > 0 &&
                string.Equals(msg.Kind, "clarify", StringComparison.OrdinalIgnoreCase))
            {
                bool isZh = _loc.CurrentCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                cardStack.Children.Add(StudioClarifyCardBuilder.Build(_shell, msg, isZh, isFollowedByNewerTurn));
            }

            if (msg.Suggestions.Count > 0 &&
                (string.Equals(msg.Kind, "clarify", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(msg.Kind, "answer", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(msg.Kind, "unsupported", StringComparison.OrdinalIgnoreCase)) &&
                (!string.Equals(msg.Kind, "clarify", StringComparison.OrdinalIgnoreCase) ||
                 (string.IsNullOrWhiteSpace(msg.SubmittedSummary) && !isFollowedByNewerTurn)))
            {
                bool isHighlightedPivotTurn =
                    string.Equals(msg.Kind, "clarify", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(msg.Kind, "unsupported", StringComparison.OrdinalIgnoreCase);
                if (isUnsupportedTurn)
                {
                    cardStack.Children.Add(new TextBlock
                    {
                        Text = _loc.T("CustomWidget.Workshop.CoCreate.UnsupportedAlternativesHint"),
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
                        Margin = new Thickness(0, 2, 0, -2)
                    });
                }
                var sugWrap = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
                foreach (string sug in msg.Suggestions)
                {
                    string captured = sug;
                    Button chip = isHighlightedPivotTurn
                        ? StudioUiPrimitives.CreateHighlightedClarifyChipButton(_shell, captured, "\uE945")
                        : StudioUiPrimitives.CreateChipButton(captured, null);
                    chip.IsEnabled = !_session.IsAnyStudioJobRunning;
                    chip.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "ChatSuggestionChip", async () =>
                    {
                        CoreVirtualKeyStates shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
                        bool editFirst = !isHighlightedPivotTurn || (shiftState & CoreVirtualKeyStates.Down) != 0;
                        if (editFirst)
                        {
                            if (_refineTextBox is not null)
                            {
                                _refineTextBox.Text = captured;
                                _refineTextBox.SelectionStart = _refineTextBox.Text.Length;
                                _refineTextBox.Focus(FocusState.Programmatic);
                            }
                            return;
                        }

                        await _shell.Generation.BeginGenerationAsync(captured);
                    });
                    sugWrap.Children.Add(chip);
                }
                cardStack.Children.Add(sugWrap);
            }

            if (msg.ActionChips.Count > 0)
            {
                var actionWrap = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
                foreach (StudioActionChip chip in msg.ActionChips)
                {
                    StudioActionChip capturedChip = chip;
                    Button actionBtn = StudioUiPrimitives.CreateChipButton(capturedChip.Label, "\uE8BD");
                    actionBtn.IsEnabled = !_session.IsAnyStudioJobRunning;
                    actionBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "ChatActionChip", async () =>
                    {
                        if (string.Equals(capturedChip.Kind, "zero_token_size", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(capturedChip.TargetValue))
                        {
                            string[] dims = capturedChip.TargetValue.Split(['x', 'X', '×'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            if (dims.Length == 2 &&
                                int.TryParse(dims[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cw) &&
                                int.TryParse(dims[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch))
                            {
                                _shell.ApplyZeroTokenMetadataEdit(newSize: new CustomWidgetSizeSpec { Width = cw, Height = ch });
                                return;
                            }
                        }

                        if (string.Equals(capturedChip.Kind, "confirm_brief_reset", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_session.PendingBriefResetUpdate is { } pendingReset)
                            {
                                CustomWidgetDesignBrief resetBrief = CustomWidgetStudioSessionStore.ApplyBriefUpdates(
                                    new CustomWidgetDesignBrief(),
                                    pendingReset);
                                _session.BriefMarkdown = CustomWidgetStudioSessionStore.FormatBriefMarkdown(
                                    resetBrief,
                                    preferEnglish: !Localized.IsActiveChinese);
                                _session.PendingBriefResetUpdate = null;
                            }
                            else
                            {
                                _session.BriefMarkdown = string.Empty;
                            }

                            if (_session.UncommittedEditCount == 0)
                            {
                                _session.UncommittedEditCount = 1;
                            }
                            _session.PersistActiveDraftIfDirty();
                            RefreshBriefButtonChrome();
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(capturedChip.Prompt))
                        {
                            await _shell.Generation.BeginGenerationAsync(capturedChip.Prompt);
                        }
                    });
                    actionWrap.Children.Add(actionBtn);
                }
                cardStack.Children.Add(actionWrap);
            }

            var assistantBubble = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(8, 8, 8, 2),
                Padding = new Thickness(13, 10, 13, 11),
                BorderThickness = new Thickness(1),
                BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
                Background = StudioUiPrimitives.ThemeBrush(_shell, "LayerFillColorAltBrush"),
                Opacity = msg.IsSuperseded ? 0.55 : 1.0,
                Child = cardStack
            };
            _retainedStreamItems[aKey] = (aState, assistantBubble);
            desiredChildren.Add(assistantBubble);
            if (linkedRevIndex >= nextManualRevCursor)
            {
                nextManualRevCursor = linkedRevIndex + 1;
            }
            if (isNewlyAdded)
            {
                FluentMotionHelper.PlayEntranceSlideAndFade(assistantBubble, offsetY: 6f, durationMs: 165);
            }
        }

        for (int r = nextManualRevCursor; r < _session.Revisions.Count; r++)
        {
            if (!aiLinkedRevIndices.Contains(r) &&
                string.Equals(_session.Revisions[r].Source, "manual", StringComparison.OrdinalIgnoreCase))
            {
                AppendRetainedManualDivider(r, _session.Revisions[r]);
            }
        }

        if (_retainedStreamItems.Count > activeKeys.Count)
        {
            foreach (string staleKey in _retainedStreamItems.Keys.Where(k => !activeKeys.Contains(k)).ToList())
            {
                _retainedStreamItems.Remove(staleKey);
            }
        }

        _lastRenderedChatMessageCount = _session.ChatMessages.Count;

        if (_session.IsInPlaceGenerating)
        {
            string pendingDisplay = !string.IsNullOrWhiteSpace(_session.PendingDisplayPrompt)
                ? _session.PendingDisplayPrompt!
                : _session.PendingPrompt;
            if (!string.IsNullOrWhiteSpace(pendingDisplay))
            {
                var pendingUserStack = new StackPanel { Spacing = 4 };
                if (!string.IsNullOrWhiteSpace(_session.InFlightTargetNodeLabel))
                {
                    var pendingTargetRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5
                    };
                    pendingTargetRow.Children.Add(new FontIcon
                    {
                        Glyph = "\uE81D",
                        FontSize = 10.5,
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    pendingTargetRow.Children.Add(new TextBlock
                    {
                        Text = _session.InFlightTargetNodeLabel,
                        FontSize = 11,
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    pendingUserStack.Children.Add(pendingTargetRow);
                }

                pendingUserStack.Children.Add(new TextBlock
                {
                    Text = pendingDisplay,
                    FontSize = 13,
                    LineHeight = 19,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });

                desiredChildren.Add(StudioUiPrimitives.CreateUserMessageBubbleChrome(_shell, pendingUserStack));
            }

            Border activeWaitingCard = _session.WaitingKind == WaitingKind.AgentMcp
                ? BuildInPlaceAgentMcpWaitingCard()
                : BuildInPlaceDirectStreamingCard();
            activeWaitingCard.SizeChanged += (_, e) =>
            {
                if (!_session.IsInPlaceGenerating || !_chatStickToBottom)
                {
                    return;
                }

                if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5)
                {
                    QueueStickyScrollToBottomChat();
                }
            };
            desiredChildren.Add(activeWaitingCard);
        }

        ReconcilePanelChildren(_chatStreamPanel, desiredChildren);

        if (_chatStickToBottom)
        {
            QueueStickyScrollToBottomChat();
        }
    }

    private static void ReconcilePanelChildren(Panel panel, IReadOnlyList<UIElement> desired)
    {
        for (int i = 0; i < desired.Count; i++)
        {
            UIElement target = desired[i];
            if (i < panel.Children.Count && ReferenceEquals(panel.Children[i], target))
            {
                continue;
            }

            int existingIdx = panel.Children.IndexOf(target);
            if (existingIdx >= 0)
            {
                panel.Children.RemoveAt(existingIdx);
            }

            panel.Children.Insert(i, target);
        }

        while (panel.Children.Count > desired.Count)
        {
            panel.Children.RemoveAt(panel.Children.Count - 1);
        }
    }

    private Border BuildInPlaceDirectStreamingCard()
    {
        var column = new StackPanel { Spacing = 10 };

        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleLeft = new Grid { ColumnSpacing = 9, VerticalAlignment = VerticalAlignment.Center };
        titleLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleLeft.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 17,
            Height = 17,
            VerticalAlignment = VerticalAlignment.Center
        });
        _directStageTitleText = new TextBlock
        {
            Text = FormatDirectStageWithDetectedName(
                ResolveDirectStageTitle(_session.InFlightStage, _directActiveProviderName),
                _session.InFlightDetectedWidgetName),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_directStageTitleText, 1);
        titleLeft.Children.Add(_directStageTitleText);
        headerGrid.Children.Add(titleLeft);

        var rightHeaderTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        TimeSpan curElapsed = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _directStartTickMs));
        _directStatsBadgeText = new TextBlock
        {
            Text = FormatDirectStatsText(_directActiveModelName, curElapsed, _directLastTokenTotal),
            FontSize = 11.5,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        rightHeaderTools.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _directStatsBadgeText
        });

        Grid.SetColumn(rightHeaderTools, 1);
        headerGrid.Children.Add(rightHeaderTools);
        column.Children.Add(headerGrid);

        // Friendly 3-step indicator
        var stepsRow = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        _directStep1Pill = CreateStreamStepPill("1 · " + _loc.T("CustomWidget.Workshop.Stream.StepUnderstand"));
        _directStep2Pill = CreateStreamStepPill("2 · " + _loc.T("CustomWidget.Workshop.Stream.StepAssemble"));
        _directStep3Pill = CreateStreamStepPill("3 · " + _loc.T("CustomWidget.Workshop.Stream.StepValidate"));
        stepsRow.Children.Add(_directStep1Pill);
        stepsRow.Children.Add(_directStep2Pill);
        stepsRow.Children.Add(_directStep3Pill);
        UpdateDirectStepPills(_session.InFlightStage);
        column.Children.Add(stepsRow);

        // Collapsible Reasoning / Thinking summary card (collapsed by default with 1-line live streaming ticker)
        var thinkingInner = new StackPanel { Spacing = 5 };
        var thinkingHeaderRow = new Grid { ColumnSpacing = 8 };
        thinkingHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        thinkingHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _directThinkingHeaderText = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Stream.ThinkingTitle"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        thinkingHeaderRow.Children.Add(_directThinkingHeaderText);

        var toggleThinkBtn = new Button
        {
            Height = 22,
            Padding = new Thickness(7, 0, 7, 0),
            FontSize = 11,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = _loc.T(_directThinkingCollapsedByUser
                ? "CustomWidget.Workshop.Stream.Expand"
                : "CustomWidget.Workshop.Stream.Collapse")
        };
        Grid.SetColumn(toggleThinkBtn, 1);
        thinkingHeaderRow.Children.Add(toggleThinkBtn);
        thinkingInner.Children.Add(thinkingHeaderRow);

        _directThinkingCollapsedPreviewText = new TextBlock
        {
            FontSize = 11.5,
            Height = 18,
            MaxLines = 1,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
            IsTextSelectionEnabled = false,
            Visibility = _directThinkingCollapsedByUser ? Visibility.Visible : Visibility.Collapsed
        };
        thinkingInner.Children.Add(_directThinkingCollapsedPreviewText);

        _directThinkingText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false
        };
        _directThinkingScroller = new ScrollViewer
        {
            Height = 152,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = _directThinkingCollapsedByUser ? Visibility.Collapsed : Visibility.Visible,
            Content = _directThinkingText
        };
        _directThinkingScroller.ViewChanged += (s, _) =>
        {
            if (_programmaticThinkingScrollDepth > 0 || s is not ScrollViewer sv)
            {
                return;
            }

            _thinkingStickToBottom = (sv.ScrollableHeight - sv.VerticalOffset) <= 20.0;
        };
        AttachNestedScrollerWheelHandoff(_directThinkingScroller, v => _thinkingStickToBottom = v);
        thinkingInner.Children.Add(_directThinkingScroller);

        toggleThinkBtn.Click += (_, _) =>
        {
            if (_directThinkingScroller is null)
            {
                return;
            }

            _directThinkingCollapsedByUser = _directThinkingScroller.Visibility == Visibility.Visible;
            _directThinkingScroller.Visibility = _directThinkingCollapsedByUser
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (_directThinkingCollapsedPreviewText is not null)
            {
                _directThinkingCollapsedPreviewText.Visibility = _directThinkingCollapsedByUser
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            toggleThinkBtn.Content = _loc.T(_directThinkingCollapsedByUser
                ? "CustomWidget.Workshop.Stream.Expand"
                : "CustomWidget.Workshop.Stream.Collapse");
            if (!_directThinkingCollapsedByUser)
            {
                _thinkingStickToBottom = true;
                QueueStickyScrollToBottomThinking();
            }
        };

        _directThinkingContainer = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 8),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush"),
            Visibility = Visibility.Collapsed,
            Child = thinkingInner
        };
        column.Children.Add(_directThinkingContainer);

        // Structured Agent Action Trace well (replaces raw JSON code dump)
        var traceInner = new StackPanel { Spacing = 8 };
        var traceHeaderRow = new Grid { ColumnSpacing = 8 };
        traceHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        traceHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        traceHeaderRow.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Stream.OutputTitle"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        _directStreamHintText = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Stream.WaitingFirstChunk"),
            FontSize = 11,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_directStreamHintText, 1);
        traceHeaderRow.Children.Add(_directStreamHintText);
        traceInner.Children.Add(traceHeaderRow);

        DeactivateTraceRowRings(_renderedTraceRows);
        _renderedTraceRows.Clear();
        _directActionTracePanel = new StackPanel { Spacing = 6 };
        RenderDirectActionTraceSteps(_inFlightTraceSteps);
        traceInner.Children.Add(_directActionTracePanel);

        column.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 9),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush"),
            Child = traceInner
        });

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(14, 11, 14, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "LayerFillColorAltBrush"),
            Child = column
        };
    }

    private Border CreateStreamStepPill(string label)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush"),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush")
            }
        };
    }

    private void UpdateDirectStepPills(AiGenerationStage stage)
    {
        int activeStep = stage switch
        {
            AiGenerationStage.Connecting or AiGenerationStage.Thinking or AiGenerationStage.ReadingWorkspace or AiGenerationStage.InspectingCapabilities or AiGenerationStage.ProbingApi => 1,
            AiGenerationStage.GeneratingJson or AiGenerationStage.PatchingWorkspace => 2,
            _ => 3
        };

        ApplyStepPillState(_directStep1Pill, stepNumber: 1, activeStep);
        ApplyStepPillState(_directStep2Pill, stepNumber: 2, activeStep);
        ApplyStepPillState(_directStep3Pill, stepNumber: 3, activeStep);
    }

    private Grid CreateManualRevisionDivider(int revIndex, StudioPersistedRevision rev)
    {
        var row = new Grid
        {
            ColumnSpacing = 10,
            Margin = new Thickness(0, 2, 0, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush")
        });

        var rightLine = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush")
        };
        Grid.SetColumn(rightLine, 2);
        row.Children.Add(rightLine);

        bool isCurrent = revIndex == _session.RevisionIndex;
        int capturedIndex = revIndex;
        string summary = string.IsNullOrWhiteSpace(rev.ChangeSummary)
            ? _loc.T("CustomWidget.Workshop.CoCreate.ManualQuickEdit")
            : rev.ChangeSummary;
        var pillBtn = new Button
        {
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, isCurrent ? "AccentFillColorDefaultBrush" : "DividerStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, isCurrent ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush"),
            IsEnabled = !_session.IsAnyStudioJobRunning,
            Content = new TextBlock
            {
                Text = $"v{revIndex + 1} · {summary}",
                FontSize = 11,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, isCurrent ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"),
                MaxWidth = 260,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        pillBtn.Click += (_, _) => SelectRevision(capturedIndex);
        Grid.SetColumn(pillBtn, 1);
        row.Children.Add(pillBtn);
        return row;
    }

    internal sealed record ReconciledTraceEntry<TRow>(
        string Id,
        StudioStreamStepState State,
        string Label,
        TRow View);

    private void RenderDirectActionTraceSteps(IReadOnlyList<StudioStreamStepItem> steps)
    {
        if (_directActionTracePanel is null)
        {
            return;
        }

        SyncActionTracePanel(
            _directActionTracePanel,
            _renderedTraceRows,
            steps,
            key => StudioUiPrimitives.ThemeBrush(_shell, key),
            _loc.T("CustomWidget.Workshop.Stream.StepConnect"));
    }

    internal static void ReconcileTraceRows<TRow>(
        List<ReconciledTraceEntry<TRow>> cache,
        IReadOnlyList<StudioStreamStepItem> steps,
        Action renderEmptyState,
        Action clearEmptyStateIfPresent,
        Func<StudioStreamStepItem, TRow> createRow,
        Action<TRow, string> updateLabel,
        Action<TRow, StudioStreamStepState> updateState,
        Action<int, TRow> removeRowAt)
    {
        if (steps.Count == 0)
        {
            renderEmptyState();
            return;
        }

        if (cache.Count == 0)
        {
            clearEmptyStateIfPresent();
        }

        int sharedPrefix = Math.Min(cache.Count, steps.Count);
        int divergeIdx = sharedPrefix;
        for (int i = 0; i < sharedPrefix; i++)
        {
            if (!string.Equals(cache[i].Id, steps[i].Id, StringComparison.Ordinal))
            {
                divergeIdx = i;
                break;
            }
        }

        int removeFrom = Math.Min(divergeIdx, steps.Count);
        for (int i = cache.Count - 1; i >= removeFrom; i--)
        {
            TRow removed = cache[i].View;
            cache.RemoveAt(i);
            removeRowAt(i, removed);
        }

        for (int i = 0; i < steps.Count; i++)
        {
            StudioStreamStepItem step = steps[i];
            if (i < cache.Count)
            {
                ReconciledTraceEntry<TRow> entry = cache[i];
                bool labelChanged = !string.Equals(entry.Label, step.Label, StringComparison.Ordinal);
                if (labelChanged)
                {
                    updateLabel(entry.View, step.Label);
                }

                // Never recreate ProgressRing when an active Running row keeps Running state across streaming chunks.
                bool stateChanged = entry.State != step.State;
                if (stateChanged)
                {
                    updateState(entry.View, step.State);
                }

                if (labelChanged || stateChanged)
                {
                    cache[i] = entry with { State = step.State, Label = step.Label };
                }
            }
            else
            {
                TRow view = createRow(step);
                cache.Add(new ReconciledTraceEntry<TRow>(step.Id, step.State, step.Label, view));
            }
        }
    }

    private static void SyncActionTracePanel(
        StackPanel panel,
        List<ReconciledTraceEntry<(Grid Row, TextBlock LabelText)>> cache,
        IReadOnlyList<StudioStreamStepItem> steps,
        Func<string, Brush> resolveBrush,
        string emptyFallbackText)
    {
        ReconcileTraceRows<(Grid Row, TextBlock LabelText)>(
            cache,
            steps,
            renderEmptyState: () =>
            {
                if (cache.Count == 0 &&
                    panel.Children.Count == 1 &&
                    panel.Children[0] is TextBlock existingEmpty)
                {
                    if (!string.Equals(existingEmpty.Text, emptyFallbackText, StringComparison.Ordinal))
                    {
                        existingEmpty.Text = emptyFallbackText;
                    }
                    return;
                }

                DeactivateTraceRowRings(cache);
                cache.Clear();
                panel.Children.Clear();
                panel.Children.Add(new TextBlock
                {
                    Text = emptyFallbackText,
                    FontSize = 11.5,
                    Foreground = resolveBrush("TextFillColorTertiaryBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
            },
            clearEmptyStateIfPresent: () =>
            {
                if (panel.Children.Count > 0)
                {
                    panel.Children.Clear();
                }
            },
            createRow: step =>
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                UIElement indicator = CreateTraceStepIndicator(step.State, resolveBrush);
                row.Children.Add(indicator);

                var labelText = new TextBlock
                {
                    Text = step.Label,
                    FontSize = 12,
                    FontWeight = step.State == StudioStreamStepState.Running ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = resolveBrush(ResolveTraceStepForegroundKey(step.State)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(labelText, 1);
                row.Children.Add(labelText);
                ToolTipService.SetToolTip(row, step.Label);

                panel.Children.Add(row);
                return (Row: row, LabelText: labelText);
            },
            updateLabel: (view, newLabel) =>
            {
                view.LabelText.Text = newLabel;
                ToolTipService.SetToolTip(view.Row, newLabel);
            },
            updateState: (view, newState) =>
            {
                if (view.Row.Children.Count > 0 && view.Row.Children[0] is ProgressRing oldRing)
                {
                    oldRing.IsActive = false;
                }
                if (view.Row.Children.Count > 0)
                {
                    view.Row.Children.RemoveAt(0);
                }
                view.Row.Children.Insert(0, CreateTraceStepIndicator(newState, resolveBrush));
                view.LabelText.FontWeight = newState == StudioStreamStepState.Running
                    ? FontWeights.SemiBold
                    : FontWeights.Normal;
                view.LabelText.Foreground = resolveBrush(ResolveTraceStepForegroundKey(newState));
            },
            removeRowAt: (index, view) =>
            {
                if (view.Row.Children.Count > 0 && view.Row.Children[0] is ProgressRing ring)
                {
                    ring.IsActive = false;
                }
                if (index < panel.Children.Count)
                {
                    panel.Children.RemoveAt(index);
                }
            });
    }

    private static string ResolveTraceStepForegroundKey(StudioStreamStepState state) =>
        state switch
        {
            StudioStreamStepState.Warning => "SystemFillColorCautionBrush",
            StudioStreamStepState.Running => "TextFillColorPrimaryBrush",
            _ => "TextFillColorSecondaryBrush"
        };

    private static UIElement CreateTraceStepIndicator(
        StudioStreamStepState state,
        Func<string, Brush> resolveBrush) =>
        state switch
        {
            StudioStreamStepState.Completed => new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 11,
                Foreground = resolveBrush("SystemFillColorSuccessBrush"),
                VerticalAlignment = VerticalAlignment.Center
            },
            StudioStreamStepState.Warning => new FontIcon
            {
                Glyph = "\uE7BA",
                FontSize = 11,
                Foreground = resolveBrush("SystemFillColorCautionBrush"),
                VerticalAlignment = VerticalAlignment.Center
            },
            StudioStreamStepState.Running => new ProgressRing
            {
                IsActive = true,
                Width = 12,
                Height = 12,
                VerticalAlignment = VerticalAlignment.Center
            },
            _ => new FontIcon
            {
                Glyph = "\uEA3A",
                FontSize = 10,
                Foreground = resolveBrush("TextFillColorTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            }
        };

    private static void DeactivateTraceRowRings(IList<ReconciledTraceEntry<(Grid Row, TextBlock LabelText)>> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            Grid row = rows[i].View.Row;
            if (row.Children.Count > 0 && row.Children[0] is ProgressRing ring)
            {
                ring.IsActive = false;
            }
        }
    }

    private void ApplyStepPillState(Border? pill, int stepNumber, int activeStep)
    {
        if (pill is null)
        {
            return;
        }

        bool isActive = stepNumber == activeStep;
        bool isCompleted = stepNumber < activeStep;
        pill.BorderThickness = new Thickness(isActive ? 0 : 1);
        pill.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush");
        pill.Background = isActive || isCompleted
            ? StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush")
            : StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush");
        if (pill.Child is TextBlock tb)
        {
            tb.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            tb.Foreground = isActive
                ? StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush")
                : (isCompleted ? StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorPrimaryBrush") : StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"));
        }
    }

    private Border BuildInPlaceAgentMcpWaitingCard()
    {
        var column = new StackPanel { Spacing = 10 };

        var titleHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleHeader.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uE756",
                FontSize = 14,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush")
            }
        });
        titleHeader.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.AgentMcp.WaitingTitle"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        column.Children.Add(titleHeader);

        var steps = new StackPanel { Spacing = 8 };
        steps.Children.Add(StudioUiPrimitives.CreateStepRow(_shell, "1", _loc.T("CustomWidget.Workshop.AgentMcp.Step1")));
        steps.Children.Add(StudioUiPrimitives.CreateStepRow(_shell, "2", _loc.T("CustomWidget.Workshop.AgentMcp.Step2")));
        steps.Children.Add(StudioUiPrimitives.CreateStepRow(_shell, "\u2713", _loc.T("CustomWidget.Workshop.AgentMcp.Step3")));
        column.Children.Add(steps);

        if (_mcpActivityItems.Count > 0)
        {
            var activityStack = new StackPanel
            {
                Spacing = 4,
                Padding = new Thickness(8, 6, 8, 6)
            };
            foreach (string act in _mcpActivityItems)
            {
                var actRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                actRow.Children.Add(new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 10.5,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "SystemFillColorSuccessBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                actRow.Children.Add(new TextBlock
                {
                    Text = act,
                    FontSize = 11.5,
                    Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                });
                activityStack.Children.Add(actRow);
            }

            column.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
                Child = activityStack
            });
        }

        var waitingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        waitingRow.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
        ConnectedMcpClientInfo? connectedClient = DeskBoxAgentBridgeServer.LastMcpClient;
        string agentWaitStatus = connectedClient is not null
            ? $"{_loc.Format("CustomWidget.Workshop.Composer.McpLastClientFormat", connectedClient.ClientName)} · {_loc.T("CustomWidget.Workshop.AgentMcp.WaitingStatus")}"
            : _loc.T("CustomWidget.Workshop.AgentMcp.WaitingStatus");
        waitingRow.Children.Add(new TextBlock
        {
            Text = agentWaitStatus,
            Style = StudioUiPrimitives.TextStyle("CaptionTextBlockStyle"),
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        column.Children.Add(waitingRow);

        var actions = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        string recopyLabel = _loc.T("CustomWidget.Workshop.AgentMcp.RecopyPrompt");
        var recopyBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            Style = StudioUiPrimitives.AccentButtonStyle(),
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE8C8", recopyLabel)
        };
        recopyBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "RecopyAgentPrompt", async () =>
        {
            if (!string.IsNullOrEmpty(_shell.CommitAndWorkspace.LastSelfCopiedText))
            {
                _shell.SetClipboardText(_shell.CommitAndWorkspace.LastSelfCopiedText);
                recopyBtn.Content = "✓ " + _loc.T("CustomWidget.Native.Dialog.CopiedButton");
                await Task.Delay(1500);
                recopyBtn.Content = StudioUiPrimitives.CreateSmallIconLabel("\uE8C8", recopyLabel);
            }
        });
        actions.Children.Add(recopyBtn);

        var connectBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE943", _loc.T("CustomWidget.Workshop.Composer.QuickSetupMcp"))
        };
        connectBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "AgentWaitingQuickConnect",
            () => _shell.ChannelOnboarding.ShowAgentMcpQuickConnectDialogAsync(RefreshCoCreationStream));
        actions.Children.Add(connectBtn);

        var guideBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE897", _loc.T("CustomWidget.Workshop.GuideButton"))
        };
        guideBtn.Click += (_, _) => _shell.ChannelOnboarding.StartChannelStepTour(_session.ResolveEffectiveStudioChannel(out _));
        actions.Children.Add(guideBtn);

        var cancelBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Content = _loc.T("Common.Cancel")
        };
        cancelBtn.Click += (_, _) => _shell.CancelWaiting();
        actions.Children.Add(cancelBtn);
        column.Children.Add(actions);

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(14, 11, 14, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "LayerFillColorAltBrush"),
            Child = column
        };
    }

    internal Flyout BuildDesignBriefFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var stack = new StackPanel { Spacing = 10, Width = 356 };

        stack.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.CoCreate.BriefDialogTitle"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.CoCreate.BriefDialogDesc"),
            Style = StudioUiPrimitives.TextStyle("CaptionTextBlockStyle"),
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 156,
            MaxHeight = 280,
            MaxLength = CustomWidgetStudioSessionStore.MaxBriefBytes,
            PlaceholderText = _loc.T("CustomWidget.Workshop.CoCreate.BriefPlaceholder")
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        stack.Children.Add(editor);

        void CommitBriefText()
        {
            string trimmed = editor.Text?.Trim() ?? string.Empty;
            if (string.Equals(trimmed, _session.BriefMarkdown, StringComparison.Ordinal))
            {
                return;
            }

            _session.BriefMarkdown = trimmed;
            if (_session.UncommittedEditCount == 0 && !string.IsNullOrWhiteSpace(_session.BriefMarkdown))
            {
                _session.UncommittedEditCount = 1;
            }
            _session.PersistActiveDraftIfDirty();
            RefreshBriefButtonChrome();
        }

        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var clearBtn = new Button
        {
            Content = _loc.T("CustomWidget.Workshop.CoCreate.BriefClear")
        };
        clearBtn.Click += (_, _) => editor.Text = string.Empty;
        footer.Children.Add(clearBtn);

        var saveBtn = new Button
        {
            Style = StudioUiPrimitives.AccentButtonStyle(),
            MinWidth = 88,
            Content = _loc.T("CustomWidget.Workshop.Footer.Save")
        };
        saveBtn.Click += (_, _) =>
        {
            CommitBriefText();
            flyout.Hide();
        };
        Grid.SetColumn(saveBtn, 2);
        footer.Children.Add(saveBtn);
        stack.Children.Add(footer);

        StudioUiPrimitives.BindFlyoutToStudioTheme(_shell, flyout, stack);
        flyout.Opening += (_, _) => editor.Text = _session.BriefMarkdown;
        flyout.Closed += (_, _) => CommitBriefText();

        flyout.Content = stack;
        return flyout;
    }

    internal async Task ConfirmAndClearCoCreationHistoryAsync()
    {
        if (_shell.IsDialogOpen || _shell.IsClosed || _shell.ShellRootGrid.XamlRoot is null)
        {
            return;
        }

        _shell.IsDialogOpen = true;
        ContentDialogResult res;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _shell.ShellRootGrid.XamlRoot,
                RequestedTheme = StudioUiPrimitives.GetStudioExplicitTheme(_shell),
                Title = _loc.T("CustomWidget.Workshop.CoCreate.ClearHistoryConfirmTitle"),
                Content = new TextBlock
                {
                    Text = _loc.T("CustomWidget.Workshop.CoCreate.ClearHistoryConfirmBody"),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = _loc.T("CustomWidget.Workshop.CoCreate.ClearHistory"),
                CloseButtonText = _loc.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) &&
                style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }

            res = await dialog.ShowAsync();
        }
        finally
        {
            _shell.IsDialogOpen = false;
        }

        if (res != ContentDialogResult.Primary)
        {
            return;
        }

        if (_session.IsEditMode && !string.IsNullOrWhiteSpace(_session.TargetWidgetId))
        {
            _sessionStore.ClearWidgetCoCreationHistory(_session.TargetWidgetId!);
        }
        else
        {
            _session.DiscardActiveDraftCleanly();
        }

        string? curManifest = _session.CurrentManifestJson;
        string? curLogicJs = _session.CurrentLogicJs;
        CustomWidgetLocaleCatalog? curLocales = _session.CurrentLocales;
        _session.BriefMarkdown = string.Empty;
        _session.ChatMessages.Clear();
        _session.Revisions.Clear();
        _session.RevisionIndex = -1;
        if (!string.IsNullOrWhiteSpace(curManifest))
        {
            _session.AddRevision(
                curManifest!,
                _loc.T("CustomWidget.Workshop.History.Current"),
                isAiChange: false,
                source: "manual",
                isCommitted: _session.IsEditMode,
                markDirty: !_session.IsEditMode,
                logicJs: curLogicJs,
                hasExplicitLogicJs: true,
                locales: curLocales,
                hasExplicitLocales: true);
        }

        _shell.StageViewport.ShowPreviewPage(justLoaded: false);
        _shell.ShowPageMessage(
            InfoBarSeverity.Success,
            string.Empty,
            _loc.T("CustomWidget.Workshop.CoCreate.HistoryCleared"),
            null);
    }

    internal void StepRevision(int delta) => SelectRevision(_session.RevisionIndex + delta);

    internal void SelectRevision(int target)
    {
        if (_session.IsAnyStudioJobRunning || target < 0 || target >= _session.Revisions.Count || _shell.StageViewport.PreviewVm is null)
        {
            return;
        }

        _session.SelectRevisionIndex(target);
        _session.InvalidateI18nSyncPlanCache();
        _shell.StageViewport.ApplyLivePreviewUpdateInPlace();
    }

    internal void RefreshRevisionChrome()
    {
        if (_undoButton is not null)
        {
            _undoButton.IsEnabled = !_session.IsAnyStudioJobRunning && _session.RevisionIndex > 0;
        }

        if (_redoButton is not null)
        {
            _redoButton.IsEnabled = !_session.IsAnyStudioJobRunning && _session.RevisionIndex < _session.Revisions.Count - 1;
        }

        if (_revisionsMenuButton is not null)
        {
            string revMenuText = _loc.Format("CustomWidget.Workshop.CoCreate.RevisionsMenu", Math.Max(1, _session.RevisionIndex + 1));
            _revisionsMenuButton.Content = StudioUiPrimitives.CreateSmallIconLabel("\uE81C", revMenuText);
            AutomationProperties.SetName(_revisionsMenuButton, revMenuText);
        }

        RefreshBriefButtonChrome();
        RefreshCoCreationStream();
    }

    private string ResolveDirectStageTitle(AiGenerationStage stage, string providerName) => stage switch
    {
        AiGenerationStage.Connecting => _loc.Format("CustomWidget.Workshop.Stream.StageConnecting", providerName),
        AiGenerationStage.Thinking => _loc.T("CustomWidget.Workshop.Stream.StageThinking"),
        AiGenerationStage.ReadingWorkspace => _loc.T("CustomWidget.Workshop.Stream.StepReadWorkspace"),
        AiGenerationStage.InspectingCapabilities => _loc.T("CustomWidget.AiApi.Stage.InspectingCapabilities"),
        AiGenerationStage.ProbingApi => _loc.T("CustomWidget.AiApi.Stage.ProbingApi"),
        AiGenerationStage.GeneratingJson or AiGenerationStage.PatchingWorkspace => _loc.T("CustomWidget.Workshop.Stream.StageGenerating"),
        AiGenerationStage.Validating => _loc.T("CustomWidget.Workshop.Stream.StageValidating"),
        AiGenerationStage.AutoRepairing => _loc.T("CustomWidget.Workshop.Stream.StageRepairing"),
        _ => _loc.T("CustomWidget.Workshop.Direct.Title")
    };

    private static string FormatDirectStageWithDetectedName(string stageTitle, string? detectedWidgetName) =>
        string.IsNullOrWhiteSpace(detectedWidgetName)
            ? stageTitle
            : $"{stageTitle} · {detectedWidgetName.Trim()}";

    private void QueueStickyScrollToBottomChat()
    {
        if (_pendingChatStickyScrollQueued || !_chatStickToBottom)
        {
            return;
        }

        _pendingChatStickyScrollQueued = true;
        _shell.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _pendingChatStickyScrollQueued = false;
            if (_shell.IsClosed || !_chatStickToBottom || _chatStreamScroller is not { } scroller)
            {
                return;
            }

            ScrollToBottomExact(scroller, ref _programmaticChatScrollDepth);
        });
    }

    private void QueueStickyScrollToBottomThinking()
    {
        if (_pendingThinkingStickyScrollQueued || !_thinkingStickToBottom)
        {
            return;
        }

        _pendingThinkingStickyScrollQueued = true;
        _shell.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _pendingThinkingStickyScrollQueued = false;
            if (_shell.IsClosed || !_thinkingStickToBottom || _directThinkingScroller is not { Visibility: Visibility.Visible } scroller)
            {
                return;
            }

            ScrollToBottomExact(scroller, ref _programmaticThinkingScrollDepth);
        });
    }

    private static void ScrollToBottomExact(ScrollViewer scroller, ref int programmaticDepth)
    {
        programmaticDepth++;
        try
        {
            scroller.ChangeView(null, double.MaxValue, null, disableAnimation: true);
        }
        finally
        {
            programmaticDepth--;
        }
    }

    private void AttachNestedScrollerWheelHandoff(ScrollViewer innerScroller, Action<bool> setStickToBottom)
    {
        innerScroller.PointerWheelChanged += (_, e) =>
        {
            var props = e.GetCurrentPoint(innerScroller).Properties;
            if (props.IsHorizontalMouseWheel)
            {
                return;
            }

            int delta = props.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }

            if (delta > 0 && innerScroller.ScrollableHeight > 1.0)
            {
                setStickToBottom(false);
            }

            bool canScrollInnerUp = delta > 0 && innerScroller.VerticalOffset > 0.5;
            bool canScrollInnerDown = delta < 0 && (innerScroller.ScrollableHeight - innerScroller.VerticalOffset) > 0.5;
            if (innerScroller.ScrollableHeight > 1.0 && (canScrollInnerUp || canScrollInnerDown))
            {
                return;
            }

            if (_chatStreamScroller is { ScrollableHeight: > 0.5 } outer)
            {
                if (delta > 0)
                {
                    _chatStickToBottom = false;
                }

                double step = -(delta / 120.0) * 72.0;
                double nextOffset = Math.Clamp(outer.VerticalOffset + step, 0.0, outer.ScrollableHeight);
                _programmaticChatScrollDepth++;
                try
                {
                    outer.ChangeView(null, nextOffset, null, disableAnimation: true);
                }
                finally
                {
                    _programmaticChatScrollDepth--;
                }

                _chatStickToBottom = (outer.ScrollableHeight - nextOffset) <= 24.0;
                e.Handled = true;
            }
        };
    }

    internal void UpdateDirectStreamProgress(AiGenerationStreamProgress progress)
    {
        _session.InFlightStage = progress.Stage;
        _directActiveModelName = progress.Model;
        _directLastCharTotal = progress.ReasoningChars + progress.ContentChars;
        int thinkTokens = progress.ThinkingTokens > 0
            ? progress.ThinkingTokens
            : (progress.ReasoningChars > 0 ? Math.Max(1, StreamingTurnAccumulator.EstimateTokenCount(progress.ThinkingText)) : 0);
        int contentTokens = progress.ContentTokens > 0
            ? progress.ContentTokens
            : (progress.ContentChars > 0 ? Math.Max(1, StreamingTurnAccumulator.EstimateTokenCount(progress.ContentText)) : 0);
        int totalOutTokens = progress.TotalOutputTokens > 0
            ? progress.TotalOutputTokens
            : thinkTokens + contentTokens;
        _directLastTokenTotal = totalOutTokens;

        if (!string.IsNullOrWhiteSpace(progress.DetectedWidgetName))
        {
            _session.InFlightDetectedWidgetName = progress.DetectedWidgetName;
            if (_shell.PreviewTheme.BlueprintTitleText is not null)
            {
                _shell.PreviewTheme.BlueprintTitleText.Text = progress.DetectedWidgetName;
            }
            if (_shell.StageViewport.NameText is not null && string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
            {
                _shell.StageViewport.NameText.Text = progress.DetectedWidgetName;
            }
        }

        string stageTitle = ResolveDirectStageTitle(progress.Stage, progress.ProviderName);
        if (_directStageTitleText is not null)
        {
            _directStageTitleText.Text = FormatDirectStageWithDetectedName(stageTitle, _session.InFlightDetectedWidgetName);
        }

        if (_shell.StageViewport.StageGeneratingStatusText is not null)
        {
            _shell.StageViewport.StageGeneratingStatusText.Text = stageTitle;
        }

        UpdateDirectStepPills(progress.Stage);

        if (_directStatsBadgeText is not null)
        {
            _directStatsBadgeText.Text = FormatDirectStatsText(
                progress.Model,
                progress.Elapsed,
                _directLastTokenTotal);
        }

        if (_directThinkingContainer is not null &&
            _directThinkingText is not null &&
            _directThinkingHeaderText is not null)
        {
            if (progress.ReasoningChars > 0 || thinkTokens > 0)
            {
                _directThinkingContainer.Visibility = Visibility.Visible;
                string thinkTokensLabel = StreamingTurnAccumulator.FormatCompactTokensWithUnit(Math.Max(1, thinkTokens));
                _directThinkingHeaderText.Text = progress.Stage == AiGenerationStage.Thinking
                    ? _loc.Format("CustomWidget.Workshop.Stream.ThinkingLive", thinkTokensLabel)
                    : _loc.Format("CustomWidget.Workshop.Stream.ThinkingDone", thinkTokensLabel);

                if (_directThinkingCollapsedPreviewText is not null)
                {
                    string latestLine = StudioManifestInspector.ExtractLatestNonEmptySingleLine(progress.ThinkingText);
                    if (!string.IsNullOrEmpty(latestLine))
                    {
                        _directThinkingCollapsedPreviewText.Text = latestLine;
                    }
                }

                string thinkTail = progress.ThinkingText.Length > 2800
                    ? "…\n" + progress.ThinkingText[^2800..]
                    : progress.ThinkingText;
                _directThinkingText.Text = thinkTail;

                if (_directThinkingScroller is { Visibility: Visibility.Visible } && _thinkingStickToBottom)
                {
                    QueueStickyScrollToBottomThinking();
                }
            }
        }

        if (progress.Steps is { Count: > 0 } steps)
        {
            _inFlightTraceSteps = steps;
            RenderDirectActionTraceSteps(_inFlightTraceSteps);
        }

        if (_directStreamHintText is not null)
        {
            string? streamHint = string.IsNullOrWhiteSpace(progress.StreamingHint)
                ? null
                : progress.StreamingHint.Trim();
            if (progress.ContentChars > 0 || contentTokens > 0)
            {
                string tokensText = _loc.Format(
                    "CustomWidget.Workshop.Stream.CharsReceived",
                    StreamingTurnAccumulator.FormatCompactTokensWithUnit(Math.Max(1, contentTokens)));
                _directStreamHintText.Text = streamHint is not null
                    ? $"{tokensText} · {streamHint}"
                    : tokensText;
            }
            else if (streamHint is not null)
            {
                _directStreamHintText.Text = streamHint;
            }
            else if (progress.Stage == AiGenerationStage.Thinking)
            {
                _directStreamHintText.Text = _loc.T("CustomWidget.Workshop.Stream.ThinkingWaitOutput");
            }
            else
            {
                _directStreamHintText.Text = stageTitle;
            }
        }
    }

    internal void ClearMcpActivities() => _mcpActivityItems.Clear();

    internal void RecordMcpActivity(string op, string detail)
    {
        string label = op switch
        {
            "mcp_hello" => string.IsNullOrWhiteSpace(detail)
                ? "MCP client connected"
                : _loc.Format("CustomWidget.Workshop.Composer.McpLastClientFormat", detail),
            "studio_context" => "deskbox_get_studio_context",
            "list" or "get" => "deskbox_list_widgets",
            "upsert" => string.Equals(detail, "commit=true", StringComparison.Ordinal)
                ? "deskbox_upsert_widget (commit)"
                : "deskbox_upsert_widget (stage)",
            "stage_commit" => "deskbox_upsert_widget (commit)",
            "capture_preview" => "deskbox_capture_widget_preview",
            "push" or "push_data" => "deskbox_push_widget_data",
            _ => op
        };

        if (_mcpActivityItems.Count > 0 && string.Equals(_mcpActivityItems[^1], label, StringComparison.Ordinal))
        {
            return;
        }

        if (_mcpActivityItems.Count >= 8)
        {
            _mcpActivityItems.RemoveAt(0);
        }
        _mcpActivityItems.Add(label);

        if (_session.IsInPlaceGenerating && _session.WaitingKind == WaitingKind.AgentMcp)
        {
            RefreshCoCreationStream();
        }
    }

    private TextBlock CreateFormattedAssistantTextBlock(string? rawText)
    {
        string text = rawText ?? string.Empty;
        var tb = new TextBlock
        {
            FontSize = 13,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        if (!text.Contains("**", StringComparison.Ordinal) && !text.Contains('`', StringComparison.Ordinal))
        {
            tb.Text = text;
            return tb;
        }

        var monoFont = new FontFamily("Cascadia Mono, Consolas, Courier New");
        int i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int endBold = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (endBold > i + 2)
                {
                    tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                    {
                        Text = text[(i + 2)..endBold],
                        FontWeight = FontWeights.SemiBold
                    });
                    i = endBold + 2;
                    continue;
                }
            }

            if (text[i] == '`')
            {
                int endCode = text.IndexOf('`', i + 1);
                if (endCode > i + 1 && text.IndexOf('\n', i + 1, endCode - i - 1) < 0)
                {
                    tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                    {
                        Text = text[(i + 1)..endCode],
                        FontFamily = monoFont,
                        FontSize = 12,
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "AccentTextFillColorPrimaryBrush")
                    });
                    i = endCode + 1;
                    continue;
                }
            }

            int nextBold = text.IndexOf("**", i + 1, StringComparison.Ordinal);
            int nextCode = text.IndexOf('`', i + 1);
            int nextSpecial = text.Length;
            if (nextBold >= 0 && nextBold < nextSpecial) nextSpecial = nextBold;
            if (nextCode >= 0 && nextCode < nextSpecial) nextSpecial = nextCode;

            tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = text[i..nextSpecial]
            });
            i = nextSpecial;
        }

        return tb;
    }

    private string? TryResolveNodePathFromDiffPill(string pill)
    {
        string? manifestJson = _session.CurrentManifestJson;
        if (string.IsNullOrWhiteSpace(pill) || string.IsNullOrWhiteSpace(manifestJson))
        {
            return null;
        }

        CustomWidgetManifest? manifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest;
        if (manifest is null)
        {
            return null;
        }

        string? targetId = null;
        int hashIdx = pill.IndexOf('#');
        if (hashIdx >= 0 && hashIdx + 1 < pill.Length)
        {
            int end = hashIdx + 1;
            while (end < pill.Length && (char.IsLetterOrDigit(pill[end]) || pill[end] is '_' or '-'))
            {
                end++;
            }
            if (end > hashIdx + 1)
            {
                targetId = pill[(hashIdx + 1)..end];
            }
        }

        if (!string.IsNullOrWhiteSpace(targetId))
        {
            return StudioManifestInspector.FindMatchingNodePath(
                manifest,
                node => string.Equals(node.Id, targetId, StringComparison.OrdinalIgnoreCase));
        }

        string rootPath = manifest.View.NodePath;
        return StudioManifestInspector.FindMatchingNodePath(
            manifest,
            node => !string.IsNullOrWhiteSpace(node.Type) &&
                    !string.Equals(node.NodePath, rootPath, StringComparison.Ordinal) &&
                    pill.Contains(node.Type, StringComparison.OrdinalIgnoreCase));
    }

    internal string FormatDirectStatsText(
        string model,
        TimeSpan elapsed,
        int liveOutputTokens,
        long promptTokens = 0,
        long cachedPromptTokens = 0,
        long completionTokens = 0)
    {
        string timeText = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
        string shortModel = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;
        if (shortModel.Length > 20)
        {
            shortModel = shortModel[..20] + "…";
        }

        if (promptTokens + completionTokens > 0)
        {
            int cachePct = promptTokens > 0 && cachedPromptTokens > 0
                ? (int)Math.Clamp(Math.Round(cachedPromptTokens * 100.0 / promptTokens), 0, 100)
                : 0;
            string promptStr = StreamingTurnAccumulator.FormatCompactTokenCount(promptTokens);
            string compStr = StreamingTurnAccumulator.FormatCompactTokenCount(completionTokens);
            string tokenText = cachePct > 0
                ? $"↑{promptStr} ({cachePct}%⚡) · ↓{compStr} tokens"
                : $"↑{promptStr} · ↓{compStr} tokens";
            return string.IsNullOrWhiteSpace(shortModel)
                ? $"{timeText} · {tokenText}"
                : $"{shortModel} · {timeText} · {tokenText}";
        }

        if (liveOutputTokens > 0)
        {
            return _loc.Format(
                "CustomWidget.Workshop.Stream.StatsWithChars",
                shortModel,
                timeText,
                StreamingTurnAccumulator.FormatCompactTokensWithUnit(liveOutputTokens));
        }

        return string.IsNullOrWhiteSpace(shortModel)
            ? timeText
            : $"{shortModel} · {timeText}";
    }

    internal void ClearPreviewElementRefs()
    {
        _chatStreamScroller = null;
        _chatStreamPanel = null;
        _stageHealthDiagnosticBar = null;
        _suggestionChipsPanel = null;
        _targetNodePillBorder = null;
        _targetNodePillText = null;
        _refineTextBox = null;
        _refineSubmitButton = null;
        _applyInPlaceButton = null;
        _commitButton = null;
        _briefHeaderButton = null;
        _undoButton = null;
        _redoButton = null;
        _revisionsMenuButton = null;
        _retainedStreamHostPanel = null;
        _retainedStreamItems.Clear();
        _directStageTitleText = null;
        _directStatsBadgeText = null;
        _directThinkingContainer = null;
        _directThinkingHeaderText = null;
        _directThinkingCollapsedPreviewText = null;
        _directThinkingScroller = null;
        _directThinkingText = null;
        DeactivateTraceRowRings(_renderedTraceRows);
        _renderedTraceRows.Clear();
        _directActionTracePanel = null;
        _directStreamHintText = null;
        _directStep1Pill = null;
        _directStep2Pill = null;
        _directStep3Pill = null;
    }

    internal void ResetRenderState()
    {
        _lastRenderedChatMessageCount = 0;
        _chatStickToBottom = true;
        _thinkingStickToBottom = true;
        _pendingChatStickyScrollQueued = false;
        _pendingThinkingStickyScrollQueued = false;
        _inFlightTraceSteps = [];
        _mcpActivityItems.Clear();
        _retainedStreamItems.Clear();
        DeactivateTraceRowRings(_renderedTraceRows);
        _renderedTraceRows.Clear();
    }

    public void Dispose()
    {
        ClearPreviewElementRefs();
        ResetRenderState();
    }
}
