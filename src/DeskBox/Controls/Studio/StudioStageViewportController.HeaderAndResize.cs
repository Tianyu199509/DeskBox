using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.Studio;

internal sealed partial class StudioStageViewportController
{
    private Grid BuildWorkspaceHeaderBar(bool hasLiveManifest, CustomWidgetManifest? effectiveManifest)
    {
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Button back = StudioUiPrimitives.CreateIconButton("\uE72B", _loc.T("CustomWidget.Workshop.Preview.Back"));
        back.Width = 32;
        back.Height = 32;
        back.CornerRadius = new CornerRadius(6);
        back.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "PreviewBack", async () =>
        {
            if (_session.IsI18nQueueRunning)
            {
                _shell.LocaleCenter.CancelI18nQueue();
            }

            if (_session.IsInPlaceGenerating)
            {
                _shell.CancelWaiting();
                if (_session.Page == StudioPage.Start)
                {
                    return;
                }
            }

            if (_session.HasUnsavedWork && !await _shell.ConfirmDiscardCleanlyAsync())
            {
                return;
            }

            string preservedIdea = _session.StartDraftIdea;
            _shell.ResetSession();
            _session.TargetWidgetId = null;
            _session.TargetWidgetName = null;
            _shell.UpdateWindowTitle();
            _session.StartDraftIdea = preservedIdea;
            _shell.StartGallery.ShowStartPage(preservedIdea);
        });
        header.Children.Add(back);

        var leftStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        _headerEmojiText = new TextBlock
        {
            Text = _shell.PreviewTheme.ResolveDisplayEmoji(effectiveManifest),
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        };
        _nameText = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 280,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        var titleInnerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleInnerRow.Children.Add(_headerEmojiText);
        titleInnerRow.Children.Add(_nameText);
        FontIcon? headerEditPencil = null;
        if (hasLiveManifest)
        {
            headerEditPencil = new FontIcon
            {
                Glyph = "\uE70F",
                FontSize = 11,
                Opacity = 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleInnerRow.Children.Add(headerEditPencil);
        }

        var titleEditBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(8, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = hasLiveManifest,
            Content = titleInnerRow
        };
        _titleEditButton = titleEditBtn;
        if (hasLiveManifest)
        {
            ToolTipService.SetToolTip(titleEditBtn, _loc.T("CustomWidget.Workshop.CoCreate.QuickHeaderTip"));
            AutomationProperties.SetName(titleEditBtn, _loc.T("CustomWidget.Workshop.CoCreate.QuickHeaderTip"));
            titleEditBtn.Flyout = _shell.StageDialogs.BuildHeaderQuickEditFlyout();
            titleEditBtn.PointerEntered += (_, _) => { if (headerEditPencil is not null) headerEditPencil.Opacity = 0.85; };
            titleEditBtn.PointerExited += (_, _) => { if (headerEditPencil is not null && titleEditBtn.FocusState == FocusState.Unfocused) headerEditPencil.Opacity = 0; };
            titleEditBtn.GotFocus += (_, _) => { if (headerEditPencil is not null) headerEditPencil.Opacity = 0.85; };
            titleEditBtn.LostFocus += (_, _) => { if (headerEditPencil is not null) headerEditPencil.Opacity = 0; };
        }
        leftStrip.Children.Add(titleEditBtn);

        _shell.ChatPanel.RevisionsMenuButton = new DropDownButton
        {
            Height = 30,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var revisionsFlyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        revisionsFlyout.Opening += (_, _) =>
        {
            revisionsFlyout.Items.Clear();
            for (int i = _session.Revisions.Count - 1; i >= 0; i--)
            {
                int capturedIndex = i;
                StudioPersistedRevision rev = _session.Revisions[i];
                var item = new ToggleMenuFlyoutItem
                {
                    Text = _loc.Format("CustomWidget.Workshop.History.Item", i + 1, rev.ChangeSummary),
                    IsChecked = i == _session.RevisionIndex
                };
                item.Click += (_, _) => _shell.ChatPanel.SelectRevision(capturedIndex);
                revisionsFlyout.Items.Add(item);
            }
        };
        _shell.ChatPanel.RevisionsMenuButton.Flyout = revisionsFlyout;
        leftStrip.Children.Add(_shell.ChatPanel.RevisionsMenuButton);

        var undoRedo = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        _shell.ChatPanel.UndoButton = StudioUiPrimitives.CreateIconButton("\uE7A7", _loc.T("CustomWidget.Workshop.Preview.Undo"));
        _shell.ChatPanel.UndoButton.Width = 30;
        _shell.ChatPanel.UndoButton.Height = 30;
        _shell.ChatPanel.UndoButton.CornerRadius = new CornerRadius(6);
        _shell.ChatPanel.RedoButton = StudioUiPrimitives.CreateIconButton("\uE7A6", _loc.T("CustomWidget.Workshop.Preview.Redo"));
        _shell.ChatPanel.RedoButton.Width = 30;
        _shell.ChatPanel.RedoButton.Height = 30;
        _shell.ChatPanel.RedoButton.CornerRadius = new CornerRadius(6);
        _shell.ChatPanel.UndoButton.Click += (_, _) => _shell.ChatPanel.StepRevision(-1);
        _shell.ChatPanel.RedoButton.Click += (_, _) => _shell.ChatPanel.StepRevision(1);
        UpdateUndoRedoButtonVisualState();
        undoRedo.Children.Add(_shell.ChatPanel.UndoButton);
        undoRedo.Children.Add(_shell.ChatPanel.RedoButton);
        leftStrip.Children.Add(undoRedo);

        Grid.SetColumn(leftStrip, 1);
        header.Children.Add(leftStrip);

        var rightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        Button sessionMoreBtn = StudioUiPrimitives.CreateIconButton("\uE712", _loc.T("CustomWidget.Workshop.More.Tooltip"));
        sessionMoreBtn.Width = 32;
        sessionMoreBtn.Height = 32;
        sessionMoreBtn.CornerRadius = new CornerRadius(6);
        var sessionMoreFlyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        sessionMoreFlyout.Opening += (_, _) =>
        {
            sessionMoreFlyout.Items.Clear();
            bool hasCurrentCode = !string.IsNullOrWhiteSpace(_session.CurrentManifestJson);
            if (hasCurrentCode)
            {
                _shell.AddMenuItem(
                    sessionMoreFlyout,
                    "\uE943",
                    "CustomWidget.Workshop.More.ViewCode",
                    _shell.StageDialogs.ShowWorkspaceCodeViewerDialogAsync);
                _shell.AddMenuItem(
                    sessionMoreFlyout,
                    "\uEDE1",
                    "CustomWidget.Workshop.More.ExportPackage",
                    async () => await _shell.CommitAndWorkspace.ExportWidgetBundleInteractiveAsync(
                        _session.CurrentManifestJson!,
                        _session.CurrentLogicJs,
                        _session.ActiveI18nWorkingCatalog ?? _session.CurrentLocales,
                        _nameText?.Text ?? _session.TargetWidgetName));

                string i18nMenuLabel = _shell.LocaleCenter.I18nHeaderLabel is { Text.Length: > 0 } lbl &&
                                       !string.Equals(lbl.Text, _loc.T("CustomWidget.Workshop.I18n.HeaderButton"), StringComparison.Ordinal)
                    ? $"{_loc.T("CustomWidget.Workshop.More.I18nMenu")} ({lbl.Text})"
                    : _loc.T("CustomWidget.Workshop.More.I18nMenu");
                var i18nSub = new MenuFlyoutSubItem
                {
                    Text = i18nMenuLabel,
                    Icon = new FontIcon { Glyph = "\uF2B7" }
                };
                var openCenterItem = new MenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.Workshop.I18n.OpenCenterMenu"),
                    Icon = new FontIcon { Glyph = "\uE8EC" }
                };
                openCenterItem.Click += (_, _) => _shell.LocaleCenter.OpenLocaleCenter(toggleIfAlreadyOpen: false);
                i18nSub.Items.Add(openCenterItem);
                i18nSub.Items.Add(new MenuFlyoutSeparator());

                var sync13Item = new MenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.Workshop.More.I18nSync13"),
                    Icon = new FontIcon { Glyph = "\uE774" },
                    IsEnabled = !_session.IsAnyStudioJobRunning
                };
                sync13Item.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
                    _shell,
                    "StageMenuI18nSync13",
                    () => _shell.LocaleCenter.RunIncrementalI18nBatchAsync(null));
                i18nSub.Items.Add(sync13Item);

                var sync2Item = new MenuFlyoutItem
                {
                    Text = _shell.LocaleCenter.FormatBilingualSyncLabel(shortLabel: false),
                    Icon = new FontIcon { Glyph = "\uE8C1" },
                    IsEnabled = !_session.IsAnyStudioJobRunning
                };
                sync2Item.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
                    _shell,
                    "StageMenuI18nSync2",
                    () => _shell.LocaleCenter.RunIncrementalI18nBatchAsync(_shell.LocaleCenter.ResolveActiveBilingualCultureCodes()));
                i18nSub.Items.Add(sync2Item);

                sessionMoreFlyout.Items.Add(i18nSub);
                sessionMoreFlyout.Items.Add(new MenuFlyoutSeparator());
            }

            _shell.AddMenuItem(
                sessionMoreFlyout,
                "\uE8A1",
                "CustomWidget.ApiDocs.Button",
                async () => await _shell.ShowApiDocsDialogInStudioAsync());
            _shell.AddMenuItem(
                sessionMoreFlyout,
                "\uE756",
                "CustomWidget.Workshop.More.CopyAgentTask",
                async () => await _shell.CommitAndWorkspace.CopyAgentKickoffPromptAsync(_shell.ChatPanel.RefineTextBox?.Text ?? string.Empty));
            sessionMoreFlyout.Items.Add(new MenuFlyoutSeparator());

            var switchModeMenuItem = new MenuFlyoutItem
            {
                Text = _loc.T("CustomWidget.Workshop.SwitchModeButton"),
                Icon = new FontIcon { Glyph = "\uE8AB" }
            };
            switchModeMenuItem.Click += (_, _) => _shell.ChannelOnboarding.ShowAgentMcpOnboardingTour(forceShow: true);
            sessionMoreFlyout.Items.Add(switchModeMenuItem);

            var guideMenuItem = new MenuFlyoutItem
            {
                Text = _loc.T("CustomWidget.Workshop.GuideButton"),
                Icon = new FontIcon { Glyph = "\uE897" }
            };
            guideMenuItem.Click += (_, _) => _shell.ChannelOnboarding.StartChannelStepTour(_session.ResolveEffectiveStudioChannel(out _));
            sessionMoreFlyout.Items.Add(guideMenuItem);

            sessionMoreFlyout.Items.Add(new MenuFlyoutSeparator());
            _shell.AddMenuItem(
                sessionMoreFlyout,
                "\uE74D",
                "CustomWidget.Workshop.CoCreate.ClearHistory",
                _shell.ChatPanel.ConfirmAndClearCoCreationHistoryAsync);
        };
        sessionMoreBtn.Flyout = sessionMoreFlyout;
        rightActions.Children.Add(sessionMoreBtn);

        _shell.LocaleCenter.I18nHeaderIcon = new FontIcon
        {
            Glyph = "\uF2B7",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        _shell.LocaleCenter.I18nHeaderLabel = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.I18n.HeaderButton"),
            FontSize = 12,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var i18nHeaderRow = new Grid { ColumnSpacing = 6 };
        i18nHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        i18nHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        i18nHeaderRow.Children.Add(_shell.LocaleCenter.I18nHeaderIcon);
        Grid.SetColumn(_shell.LocaleCenter.I18nHeaderLabel, 1);
        i18nHeaderRow.Children.Add(_shell.LocaleCenter.I18nHeaderLabel);

        _shell.LocaleCenter.I18nHeaderButton = new Button
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            IsEnabled = hasLiveManifest,
            Visibility = (_session.IsLocaleCenterOpen || _session.IsI18nQueueRunning)
                ? Visibility.Visible
                : Visibility.Collapsed,
            Content = i18nHeaderRow
        };
        _shell.LocaleCenter.I18nHeaderButton.Click += (_, _) => _shell.LocaleCenter.OpenLocaleCenter(toggleIfAlreadyOpen: true);
        rightActions.Children.Add(_shell.LocaleCenter.I18nHeaderButton);

        _shell.ChatPanel.BriefHeaderButton = new Button
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Flyout = _shell.ChatPanel.BuildDesignBriefFlyout()
        };
        _shell.ChatPanel.RefreshBriefButtonChrome();
        rightActions.Children.Add(_shell.ChatPanel.BriefHeaderButton);

        _shell.ChatPanel.ApplyInPlaceButton = null;

        bool isVisibleOnDesktop = IsTargetWidgetVisibleOnDesktop();
        (string commitGlyph, string commitLabelKey) = ResolveCommitButtonPresentation(_session.IsEditMode, isVisibleOnDesktop);

        var commit = new Button
        {
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(6),
            Style = StudioUiPrimitives.AccentButtonStyle(),
            IsEnabled = hasLiveManifest && !_session.IsAnyStudioJobRunning,
            Content = StudioUiPrimitives.CreateSmallIconLabel(
                commitGlyph,
                _loc.T(commitLabelKey))
        };
        commit.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "PreviewHeaderCommit",
            () => _shell.CommitAndWorkspace.CommitAsync(commit, closeAfterCommit: false));
        _shell.ChatPanel.CommitButton = commit;
        rightActions.Children.Add(commit);

        Grid.SetColumn(rightActions, 2);
        header.Children.Add(rightActions);
        return header;
    }

    internal static (string Glyph, string LabelKey) ResolveCommitButtonPresentation(bool isEditMode, bool isVisibleOnDesktop)
    {
        bool showSave = isEditMode && isVisibleOnDesktop;
        return showSave
            ? ("\uE74E", "CustomWidget.Workshop.Footer.Save")
            : ("\uE710", "CustomWidget.Workshop.Footer.Place");
    }

    internal bool IsTargetWidgetVisibleOnDesktop()
    {
        if (!_session.IsEditMode || string.IsNullOrWhiteSpace(_session.TargetWidgetId) || !_hostBridge.HasWidgetManager)
        {
            return false;
        }

        string targetId = _session.TargetWidgetId!;
        IReadOnlyList<GlanceWidgetInstanceInfo> instances = _hostBridge.GetCustomWidgetInstances();
        for (int i = 0; i < instances.Count; i++)
        {
            if (string.Equals(instances[i].Id, targetId, StringComparison.OrdinalIgnoreCase))
            {
                return instances[i].IsEnabled;
            }
        }

        WidgetConfig? cfg = _hostBridge.ResolveCustomWidgetConfig(targetId);
        return cfg is not null && !cfg.IsDisabled;
    }

    internal void UpdateUndoRedoButtonVisualState()
    {
        bool busy = _session.IsAnyStudioJobRunning;
        if (_shell.ChatPanel.UndoButton is { } undoBtn)
        {
            undoBtn.IsEnabled = !busy && _session.CanUndo;
        }

        if (_shell.ChatPanel.RedoButton is { } redoBtn)
        {
            redoBtn.IsEnabled = !busy && _session.CanRedo;
        }
    }

    internal void SyncHeaderCommitActionsForCurrentMode()
    {
        bool hasLiveManifest = !string.IsNullOrWhiteSpace(_session.CurrentManifestJson);
        if (_shell.ChatPanel.CommitButton is { } commitBtn)
        {
            (string commitGlyph, string commitLabelKey) = ResolveCommitButtonPresentation(
                _session.IsEditMode,
                IsTargetWidgetVisibleOnDesktop());
            commitBtn.Content = StudioUiPrimitives.CreateSmallIconLabel(
                commitGlyph,
                _loc.T(commitLabelKey));
            commitBtn.IsEnabled = hasLiveManifest && !_session.IsAnyStudioJobRunning;
        }

        if (_shell.ChatPanel.ApplyInPlaceButton is { } existingApply)
        {
            existingApply.Visibility = Visibility.Collapsed;
            existingApply.IsEnabled = false;
        }
    }

    private CustomWidgetSizeSpec ResolveCurrentPreviewWidgetSize()
    {
        CustomWidgetManifest? manifest = ResolvePreviewManifestWithSidecar(_session.CurrentManifestJson);
        return manifest?.DefaultSize ?? new CustomWidgetSizeSpec();
    }

    internal void RefreshSizeQuickButtonChrome(CustomWidgetSizeSpec? sizeOverride = null)
    {
        if (_sizeQuickButton is null)
        {
            return;
        }

        CustomWidgetSizeSpec size = sizeOverride ?? ResolveCurrentPreviewWidgetSize();
        string sizeLabel = _currentStageZoomPercent is int pct
            ? $"{size.Width}×{size.Height} · {pct}%"
            : $"{size.Width}×{size.Height}";
        string tip = _currentStageZoomPercent is int tipPct
            ? $"{_loc.T("CustomWidget.Workshop.CoCreate.SizeButtonTip")}\n{_loc.Format("CustomWidget.Workshop.Stage.ZoomBadgeFormat", tipPct)}"
            : _loc.T("CustomWidget.Workshop.CoCreate.SizeButtonTip");

        _sizeQuickButton.Content = StudioUiPrimitives.CreateSmallIconLabel("\uE740", sizeLabel);
        ToolTipService.SetToolTip(_sizeQuickButton, tip);
        AutomationProperties.SetName(_sizeQuickButton, tip);
    }

    private Grid BuildStageTopToolbar(CustomWidgetSizeSpec currentSize, bool hasLiveManifest)
    {
        var bar = new Grid
        {
            Padding = new Thickness(10, 8, 10, 4),
            ColumnSpacing = 6
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftTools = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 6,
            VerticalSpacing = 6,
            VerticalAlignment = VerticalAlignment.Top
        };

        _pickElementIcon = new FontIcon
        {
            Glyph = "\uE81D",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        _pickElementLabel = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.CoCreate.PickElement"),
            FontSize = 12,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var pickContentRow = new Grid { ColumnSpacing = 5 };
        pickContentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickContentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickContentRow.Children.Add(_pickElementIcon);
        Grid.SetColumn(_pickElementLabel, 1);
        pickContentRow.Children.Add(_pickElementLabel);

        var pickToggle = new Button
        {
            Height = 28,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            IsEnabled = hasLiveManifest,
            Content = pickContentRow
        };
        _pickElementButton = pickToggle;
        if (!hasLiveManifest)
        {
            _session.IsInspectorModeEnabled = false;
        }
        ToolTipService.SetToolTip(pickToggle, _loc.T("CustomWidget.Workshop.CoCreate.PickElementTip"));
        AutomationProperties.SetName(pickToggle, _loc.T("CustomWidget.Workshop.CoCreate.PickElement"));
        pickToggle.Click += (_, _) =>
        {
            _session.IsInspectorModeEnabled = !_session.IsInspectorModeEnabled;
            if (!_session.IsInspectorModeEnabled)
            {
                _session.SelectedTargetNode = null;
            }

            if (_previewContent is not null)
            {
                _previewContent.SelectedNodePath = _session.SelectedTargetNode?.NodePath;
                _previewContent.IsNodeSelectionEnabled = _session.IsInspectorModeEnabled;
            }

            RefreshStageToolbarChrome();
            _shell.ChatPanel.RefreshTargetNodePillChrome();
        };
        leftTools.Children.Add(pickToggle);

        _sampleDataToggleButton = new Button
        {
            Height = 28,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed
        };
        _sampleDataToggleButton.Click += (_, _) => ToggleSampleDataPreviewMode();
        leftTools.Children.Add(_sampleDataToggleButton);

        _settingsPreviewButton = new Button
        {
            Height = 28,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            IsEnabled = hasLiveManifest,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            Flyout = _shell.StageDialogs.BuildSettingsPreviewFlyout()
        };
        leftTools.Children.Add(_settingsPreviewButton);

        _fileDropTestButton = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            IsEnabled = hasLiveManifest,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            Content = new FontIcon { Glyph = "\uE8E5", FontSize = 12 }
        };
        ToolTipService.SetToolTip(_fileDropTestButton, _loc.T("CustomWidget.Workshop.Stage.TestFileDropTip"));
        AutomationProperties.SetName(_fileDropTestButton, _loc.T("CustomWidget.Workshop.Stage.TestFileDrop"));
        _fileDropTestButton.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "TestStageFileDrop",
            TestStageFileDropFromPickerAsync);
        leftTools.Children.Add(_fileDropTestButton);

        _shell.LocaleCenter.PreviewCultureOverridePillButton = new Button
        {
            Height = 28,
            Padding = new Thickness(9, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };
        _shell.LocaleCenter.PreviewCultureOverridePillButton.Click += (_, _) => _shell.LocaleCenter.SetActivePreviewCultureOverride(null);
        leftTools.Children.Add(_shell.LocaleCenter.PreviewCultureOverridePillButton);

        bar.Children.Add(leftTools);

        if (hasLiveManifest && !_prefs.HasSeenPickElementTip && !_session.IsInPlaceGenerating)
        {
            var pickTip = new TeachingTip
            {
                Target = pickToggle,
                RequestedTheme = StudioUiPrimitives.GetStudioExplicitTheme(_shell),
                Title = _loc.T("CustomWidget.Workshop.CoCreate.PickTeachingTitle"),
                Subtitle = _loc.T("CustomWidget.Workshop.CoCreate.PickTeachingSubtitle"),
                PreferredPlacement = TeachingTipPlacementMode.Bottom,
                IsLightDismissEnabled = true
            };
            bar.Children.Add(pickTip);
            pickToggle.Loaded += (_, _) =>
            {
                if (_shell.IsClosed || _prefs.HasSeenPickElementTip)
                {
                    return;
                }

                _prefs.MarkPickElementTipSeen();
                _shell.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_shell.IsClosed &&
                        _session.Page == StudioPage.Preview &&
                        pickTip.IsLoaded &&
                        pickTip.XamlRoot is not null)
                    {
                        pickTip.RequestedTheme = StudioUiPrimitives.GetStudioExplicitTheme(_shell);
                        pickTip.IsOpen = true;
                    }
                });
            };
        }

        var rightTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Top
        };

        _sizeQuickButton = new Button
        {
            Height = 28,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            IsEnabled = hasLiveManifest,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(0)
        };
        RefreshSizeQuickButtonChrome(currentSize);
        _sizeQuickButton.Flyout = _shell.StageDialogs.BuildSizeQuickEditFlyout();
        rightTools.Children.Add(_sizeQuickButton);

        _resetPreviewStateButton = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            IsEnabled = hasLiveManifest && !_session.IsInPlaceGenerating,
            Content = new FontIcon { Glyph = "\uE777", FontSize = 12 }
        };
        ToolTipService.SetToolTip(_resetPreviewStateButton, _loc.T("CustomWidget.Workshop.Stage.ResetPreviewStateTip"));
        AutomationProperties.SetName(_resetPreviewStateButton, _loc.T("CustomWidget.Workshop.Stage.ResetPreviewStateTip"));
        _resetPreviewStateButton.Click += (_, _) => ResetPreviewRuntimeState();
        rightTools.Children.Add(_resetPreviewStateButton);

        _stageAppearanceButton = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Content = new FontIcon { Glyph = "\uE790", FontSize = 12 },
            Flyout = BuildStageAppearanceFlyout()
        };
        rightTools.Children.Add(_stageAppearanceButton);

        Grid.SetColumn(rightTools, 1);
        bar.Children.Add(rightTools);
        return bar;
    }

    private Flyout BuildStageAppearanceFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var stack = new StackPanel { Spacing = 10, Width = 288 };

        stack.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Stage.AppearanceBackdropHeader"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush")
        });
        ToolkitControls.Segmented backdropSegmented = BuildStageBackdropSegmented();
        stack.Children.Add(backdropSegmented);

        stack.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Stage.AppearanceWidgetHeader"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush")
        });
        ToolkitControls.Segmented widgetThemeSegmented = BuildStageWidgetThemeSegmented();
        stack.Children.Add(widgetThemeSegmented);

        stack.Children.Add(new TextBlock
        {
            Text = _loc.T("Widget.ChromeMode.Title"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush")
        });
        ToolkitControls.Segmented chromeModeSegmented = BuildStageChromeModeSegmented();
        stack.Children.Add(chromeModeSegmented);

        StudioUiPrimitives.BindFlyoutToStudioTheme(_shell, flyout, stack);
        flyout.Opening += (_, _) =>
        {
            StageBackdropMode effectiveBackdrop = _session.StageBackdropMode == StageBackdropMode.Wallpaper && !_session.HasSystemWallpaperAvailable
                ? (Win32Helper.IsSystemDarkMode() ? StageBackdropMode.Dark : StageBackdropMode.Light)
                : _session.StageBackdropMode;
            foreach (ToolkitControls.SegmentedItem item in backdropSegmented.Items.OfType<ToolkitControls.SegmentedItem>())
            {
                if (item.Tag is StageBackdropMode m && m == effectiveBackdrop)
                {
                    backdropSegmented.SelectedItem = item;
                    break;
                }
            }

            foreach (ToolkitControls.SegmentedItem item in widgetThemeSegmented.Items.OfType<ToolkitControls.SegmentedItem>())
            {
                if (item.Tag is string themeKey && string.Equals(themeKey, _session.StageWidgetTheme, StringComparison.OrdinalIgnoreCase))
                {
                    widgetThemeSegmented.SelectedItem = item;
                    break;
                }
            }

            foreach (ToolkitControls.SegmentedItem item in chromeModeSegmented.Items.OfType<ToolkitControls.SegmentedItem>())
            {
                if (item.Tag is WidgetChromeMode chromeMode && chromeMode == _session.StageChromeMode)
                {
                    chromeModeSegmented.SelectedItem = item;
                    break;
                }
            }
        };

        flyout.Content = stack;
        return flyout;
    }

    private ToolkitControls.Segmented BuildSegmentedRow<T>(
        IReadOnlyList<(T Value, IconElement? Icon, string LabelKey)> items,
        Func<T, bool> isInitiallySelected,
        Action<T> onSelected,
        double height = 30,
        HorizontalAlignment horizontalAlignment = HorizontalAlignment.Stretch,
        bool equalItemWidths = true,
        bool isEnabled = true)
    {
        var segmented = new ToolkitControls.Segmented
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = height,
            Height = height,
            IsEnabled = isEnabled,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach ((T value, IconElement? icon, string labelKey) in items)
        {
            string label = _loc.T(labelKey);
            var segItem = new ToolkitControls.SegmentedItem
            {
                Tag = value,
                Content = label,
                FontSize = 11.5
            };
            if (icon is not null)
            {
                segItem.Icon = icon;
            }
            ToolTipService.SetToolTip(segItem, label);
            AutomationProperties.SetName(segItem, label);
            segmented.Items.Add(segItem);
            if (isInitiallySelected(value))
            {
                segmented.SelectedItem = segItem;
            }
        }

        WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(segmented);
        if (equalItemWidths)
        {
            WidgetSegmentedLayoutHelper.ApplyEqualItemWidths(segmented);
        }
        segmented.ActualThemeChanged += (_, _) =>
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(segmented);
        segmented.SelectionChanged += (_, _) =>
        {
            if (segmented.SelectedItem is ToolkitControls.SegmentedItem { Tag: T nextValue })
            {
                onSelected(nextValue);
            }
        };

        return segmented;
    }

    private ToolkitControls.Segmented BuildStageChromeModeSegmented()
    {
        _stageChromeModeSegmented = BuildSegmentedRow<WidgetChromeMode>(
            [
                (WidgetChromeMode.System, null, "CustomWidget.Workshop.Stage.ThemeAuto"),
                (WidgetChromeMode.Standard, null, "Widget.ChromeMode.Standard"),
                (WidgetChromeMode.Compact, null, "Widget.ChromeMode.Compact"),
                (WidgetChromeMode.Overlay, null, "Widget.ChromeMode.Overlay")
            ],
            mode => mode == _session.StageChromeMode,
            nextMode =>
            {
                if (nextMode != _session.StageChromeMode)
                {
                    _session.StageChromeMode = nextMode;
                    _session.HasDirtyChromeModeOverride = true;
                    _shell.PreviewTheme.ApplyStageBackdropVisuals();
                }
            });
        return _stageChromeModeSegmented;
    }

    private ToolkitControls.Segmented BuildStageWidgetThemeSegmented()
    {
        _stageWidgetThemeSegmented = BuildSegmentedRow<string>(
            [
                (CustomWidgetStudioPreferencesStore.StageThemeAuto, new FontIcon { Glyph = "\uE790", FontSize = 11.5 }, "CustomWidget.Workshop.Stage.ThemeAuto"),
                (CustomWidgetStudioPreferencesStore.StageThemeLight, new FontIcon { Glyph = "\uE706", FontSize = 11.5 }, "CustomWidget.Workshop.Stage.ThemeLight"),
                (CustomWidgetStudioPreferencesStore.StageThemeDark, new FontIcon { Glyph = "\uE708", FontSize = 11.5 }, "CustomWidget.Workshop.Stage.ThemeDark")
            ],
            themeKey => string.Equals(themeKey, _session.StageWidgetTheme, StringComparison.OrdinalIgnoreCase),
            nextTheme =>
            {
                if (!string.Equals(nextTheme, _session.StageWidgetTheme, StringComparison.OrdinalIgnoreCase))
                {
                    _session.StageWidgetTheme = nextTheme;
                    _prefs.SetStageWidgetTheme(_session.StageWidgetTheme);
                    _shell.PreviewTheme.ApplyStageBackdropVisuals();
                }
            });
        return _stageWidgetThemeSegmented;
    }

    private ToolkitControls.Segmented BuildStageBackdropSegmented()
    {
        StageBackdropMode effectiveBackdrop = _session.StageBackdropMode == StageBackdropMode.Wallpaper && !_session.HasSystemWallpaperAvailable
            ? (Win32Helper.IsSystemDarkMode() ? StageBackdropMode.Dark : StageBackdropMode.Light)
            : _session.StageBackdropMode;

        var items = new List<(StageBackdropMode Value, IconElement? Icon, string LabelKey)>(3);
        if (_session.HasSystemWallpaperAvailable)
        {
            items.Add((StageBackdropMode.Wallpaper, new FontIcon { Glyph = "\uEB9F", FontSize = 11.5 }, "CustomWidget.Workshop.Stage.BackdropWallpaper"));
        }
        items.Add((StageBackdropMode.Light, new FontIcon { Glyph = "\uE706", FontSize = 11.5 }, "CustomWidget.Workshop.Stage.BackdropLight"));
        items.Add((StageBackdropMode.Dark, new FontIcon { Glyph = "\uE708", FontSize = 11.5 }, "CustomWidget.Workshop.Stage.BackdropDark"));

        _stageBackdropSegmented = BuildSegmentedRow(
            items,
            mode => mode == effectiveBackdrop,
            nextMode =>
            {
                if (nextMode != _session.StageBackdropMode)
                {
                    _session.StageBackdropMode = nextMode;
                    _shell.PreviewTheme.ApplyStageBackdropVisuals();
                }
            });
        return _stageBackdropSegmented;
    }

    private ToolkitControls.Segmented BuildStageViewportModeSegmented(bool hasLiveManifest)
    {
        bool initialCapsule = _session.IsCapsulePreviewMode && hasLiveManifest;
        _stageViewportModeSegmented = BuildSegmentedRow<bool>(
            [
                (false, new FontIcon { Glyph = "\uE737", FontSize = 11.5 }, "CustomWidget.Workshop.Stage.ViewExpanded"),
                (true, StudioUiPrimitives.CreateCapsuleViewportIcon(), "CustomWidget.Workshop.Stage.ViewCapsule")
            ],
            capsuleMode => capsuleMode == initialCapsule,
            nextCapsuleMode =>
            {
                if (nextCapsuleMode != _session.IsCapsulePreviewMode)
                {
                    _session.IsCapsulePreviewMode = nextCapsuleMode;
                    if (_session.IsCapsulePreviewMode && !string.IsNullOrWhiteSpace(_activeStudioPopoverPreviewId))
                    {
                        _activeStudioPopoverPreviewId = null;
                        if (_previewContent is not null)
                        {
                            _previewContent.ActivePopoverPreviewId = null;
                        }
                    }

                    RefreshPopoverAndDropToolbarChrome();
                    RefreshStageViewportModeVisuals(animateTransition: true);
                }
            },
            height: 28,
            horizontalAlignment: HorizontalAlignment.Center,
            equalItemWidths: false,
            isEnabled: hasLiveManifest);
        return _stageViewportModeSegmented;
    }

    private Grid BuildStageViewportHostWithResizeHandles(Border widgetFrame, Border capsulePreviewFrame)
    {
        var viewportHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        viewportHost.Children.Add(widgetFrame);
        viewportHost.Children.Add(capsulePreviewFrame);

        var handlesOverlay = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = !_session.IsCapsulePreviewMode && string.IsNullOrWhiteSpace(_activeStudioPopoverPreviewId)
                ? Visibility.Visible
                : Visibility.Collapsed
        };

        FrameworkElement CreateResizeGrip(bool resizeWidth, bool resizeHeight)
        {
            InputSystemCursorShape cursorShape = resizeWidth && resizeHeight
                ? InputSystemCursorShape.SizeNorthwestSoutheast
                : (resizeWidth ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);

            var gripVisual = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = StudioUiPrimitives.ThemeBrush(_shell, "AccentFillColorDefaultBrush"),
                Opacity = 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) },
                Width = resizeWidth && resizeHeight ? 8 : (resizeWidth ? 3 : 28),
                Height = resizeWidth && resizeHeight ? 8 : (resizeHeight ? 3 : 28),
                HorizontalAlignment = resizeWidth && resizeHeight ? HorizontalAlignment.Right : HorizontalAlignment.Center,
                VerticalAlignment = resizeWidth && resizeHeight ? VerticalAlignment.Bottom : VerticalAlignment.Center,
                Margin = resizeWidth && resizeHeight ? new Thickness(0, 0, 2, 2) : new Thickness(0)
            };

            var hitZone = new Grid
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Width = resizeWidth && resizeHeight ? 18 : (resizeWidth ? 12 : double.NaN),
                Height = resizeWidth && resizeHeight ? 18 : (resizeHeight ? 12 : double.NaN),
                HorizontalAlignment = resizeWidth ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                VerticalAlignment = resizeHeight ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
                Margin = resizeWidth && !resizeHeight
                    ? new Thickness(0, 12, -6, 18)
                    : (!resizeWidth && resizeHeight ? new Thickness(12, 0, 18, -6) : new Thickness(0, 0, -4, -4)),
                ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY
            };
            try
            {
                UiElementCursorHelper.SetCursor(hitZone, cursorShape);
            }
            catch
            {
            }
            hitZone.Children.Add(gripVisual);

            double startW = 0;
            double startH = 0;
            double rawDeltaX = 0;
            double rawDeltaY = 0;
            int minW = 160;
            int minH = 96;

            hitZone.PointerEntered += (_, _) => gripVisual.Opacity = 0.72;
            hitZone.PointerExited += (_, _) => gripVisual.Opacity = 0;
            hitZone.ManipulationStarted += (_, e) =>
            {
                if (_widgetFrame is null || _session.IsAnyStudioJobRunning)
                {
                    return;
                }

                e.Handled = true;
                gripVisual.Opacity = 1.0;
                startW = _widgetFrame.Width > 0 ? _widgetFrame.Width : _widgetFrame.ActualWidth;
                startH = _widgetFrame.Height > 0 ? _widgetFrame.Height : _widgetFrame.ActualHeight;
                rawDeltaX = 0;
                rawDeltaY = 0;
                CustomWidgetManifest? curManifest = ResolvePreviewManifestWithSidecar(_session.CurrentManifestJson);
                minW = Math.Clamp(curManifest?.MinSize?.Width ?? 160, 160, 640);
                minH = Math.Clamp(curManifest?.MinSize?.Height ?? 96, 96, 900);
                if (_resizeDimensionPill is not null && _resizeDimensionText is not null)
                {
                    _resizeDimensionText.Text = $"{(int)Math.Round(startW)} × {(int)Math.Round(startH)}";
                    _resizeDimensionPill.Visibility = Visibility.Visible;
                }
            };
            hitZone.ManipulationDelta += (_, e) =>
            {
                if (_widgetFrame is null || startW <= 0 || startH <= 0)
                {
                    return;
                }

                e.Handled = true;
                double zoomScale = _currentStageZoomPercent is int pct and > 0 ? pct / 100.0 : 1.0;
                rawDeltaX += e.Delta.Translation.X / zoomScale;
                rawDeltaY += e.Delta.Translation.Y / zoomScale;

                bool fineControl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0 ||
                                   (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
                int step = fineControl ? 1 : 10;

                int nextW = (int)Math.Round(startW);
                int nextH = (int)Math.Round(startH);
                if (resizeWidth)
                {
                    double candidateW = startW + rawDeltaX;
                    nextW = Math.Clamp((int)(Math.Round(candidateW / step) * step), minW, 640);
                    _widgetFrame.Width = nextW;
                }
                if (resizeHeight)
                {
                    double candidateH = startH + rawDeltaY;
                    nextH = Math.Clamp((int)(Math.Round(candidateH / step) * step), minH, 900);
                    _widgetFrame.Height = nextH;
                }

                if (_resizeDimensionText is not null)
                {
                    _resizeDimensionText.Text = $"{nextW} × {nextH}";
                }
                RefreshSizeQuickButtonChrome(new CustomWidgetSizeSpec { Width = nextW, Height = nextH });
            };
            hitZone.ManipulationCompleted += (_, e) =>
            {
                gripVisual.Opacity = 0;
                if (_resizeDimensionPill is not null)
                {
                    _resizeDimensionPill.Visibility = Visibility.Collapsed;
                }

                if (_widgetFrame is null || startW <= 0 || startH <= 0)
                {
                    return;
                }

                e.Handled = true;
                int finalW = (int)Math.Round(_widgetFrame.Width);
                int finalH = (int)Math.Round(_widgetFrame.Height);
                startW = 0;
                startH = 0;
                if (finalW >= 160 && finalH >= 96)
                {
                    _shell.ApplyZeroTokenMetadataEdit(null, null, new CustomWidgetSizeSpec { Width = finalW, Height = finalH });
                }
            };

            return hitZone;
        }

        handlesOverlay.Children.Add(CreateResizeGrip(resizeWidth: true, resizeHeight: false));
        handlesOverlay.Children.Add(CreateResizeGrip(resizeWidth: false, resizeHeight: true));
        handlesOverlay.Children.Add(CreateResizeGrip(resizeWidth: true, resizeHeight: true));

        _resizeDimensionText = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorPrimaryBrush")
        };
        _resizeDimensionPill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SolidBackgroundFillColorBaseBrush"),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "AccentFillColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _resizeDimensionText
        };
        handlesOverlay.Children.Add(_resizeDimensionPill);

        _resizeHandlesOverlay = handlesOverlay;
        viewportHost.Children.Add(handlesOverlay);
        return viewportHost;
    }
}
