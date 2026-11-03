using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    private bool _isCloseWidgetPending;

    private WidgetChromeMode ResolveTitleBarChromeMode() =>
        App.Current.WidgetManager?.ResolveWidgetChromeMode(_config, _descriptor) ??
        _chromeModeResolver.Resolve(_config, _descriptor);

    protected override void OnWidgetGroupPresentationChanged(WidgetGroupPresentation? presentation)
    {
        if (!IsClosing && ContentWidgetShell.ChromeMode != ResolveTitleBarChromeMode())
        {
            ApplyTitleBarLayout();
        }
    }

    private void ApplyTitleBarLayout()
    {
        WidgetChromeMode chromeMode = ResolveTitleBarChromeMode();
        double titleTextSize = chromeMode == WidgetChromeMode.Compact
            ? SettingsService.NormalizeTextSize(SettingsService.Settings.TextSize)
            : _titleViewModel.TitleTextSize;
        var metrics = WidgetTitleBarMetricsCalculator.Create(
            _titleViewModel.TitleIconSize,
            titleTextSize,
            includeInnerPadding: false,
            chromeMode);

        ContentWidgetShell.ApplyTitleBarMetrics(metrics, chromeMode);
        ApplyTitleActionButtonConfiguration();
        ApplyLockActionIconState();
    }

    private void ApplyTitleActionButtonConfiguration()
    {
        var actions = SettingsService.ParseWidgetHoverButtonActions(SettingsService.Settings.WidgetHoverButtonActions);
        ContentWidgetShell.PositionLockActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionLockPosition)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentWidgetShell.SizeLockActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionLockSize)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentWidgetShell.ShowAddButton =
            actions.Contains(SettingsService.WidgetHoverActionAdd);
        ContentWidgetShell.MoreActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionMore)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentWidgetShell.CloseActionButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionDelete)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyLockActionIconState()
    {
        WidgetActionIconHelper.ApplyLockState(
            ContentWidgetShell.PositionLockActionIcon,
            ContentWidgetShell.PositionLockFilledActionIcon,
            _config.IsPositionLocked,
            ContentWidgetShell.SizeLockActionIcon,
            ContentWidgetShell.SizeLockFilledActionIcon,
            _config.IsSizeLocked);
        // The lock button accessible names flip between lock/unlock wording.
        ApplyTitleActionAccessibleNames();
    }

    // ── Button click handlers ──────────────────────────────────

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        ShowWidgetCreateFlyout();
    }

    private void ShowWidgetCreateFlyout()
    {
        var localization = App.Current.LocalizationService;
        var flyout = new MenuFlyout();

        var createWidgetItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.NewWidget"),
            Icon = new SymbolIcon(Symbol.Add)
        };
        createWidgetItem.Click += async (_, _) =>
        {
            if (App.Current.WidgetManager is { } widgetManager)
            {
                await widgetManager.CreateWidgetOfKindAsync(WidgetKind.File);
            }
        };
        flyout.Items.Add(createWidgetItem);

        bool mapFolderWhenClosed = false;
        var mapFolderItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.NewFolderMapping"),
            Icon = new SymbolIcon(Symbol.OpenFile)
        };
        mapFolderItem.Click += (_, _) =>
        {
            mapFolderWhenClosed = true;
        };
        flyout.Items.Add(mapFolderItem);

        var addFeatureWidgetItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.AddFeatureWidget"),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        addFeatureWidgetItem.Click += (_, _) =>
            App.Current.ShowSettings("FeatureWidgets");
        flyout.Items.Add(addFeatureWidgetItem);

        var createAiWidgetItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.CreateAiWidget"),
            Icon = new FontIcon { Glyph = "\uE945" }
        };
        createAiWidgetItem.Click += (_, _) =>
            App.Current.ShowCustomWidgetStudio();
        flyout.Items.Add(createAiWidgetItem);

        flyout.Closed += (_, _) =>
        {
            if (!mapFolderWhenClosed)
            {
                return;
            }

            mapFolderWhenClosed = false;
            IntPtr preferredOwner = HWnd;
            _ = DispatcherQueue.TryEnqueue(async () =>
                await App.Current.CreateFolderWidgetFromPickerAsync(preferredOwner));
        };

        ShowFlyoutWithInteraction(
            flyout,
            ContentWidgetShell.AddActionButton);
    }

    /// <summary>
    /// Programmatically opens the content-specific new-item editor.
    /// The title bar add button is reserved for the global widget creation menu.
    /// </summary>
    internal void TriggerAddAction()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_contentHost.CurrentContent is IWidgetAddActionContent addActionContent)
            {
                PrepareSurfaceForExternalInteraction();
                await addActionContent.AddFromTitleButtonAsync();
            }
        });
    }

    internal void PrepareSurfaceForExternalInteraction()
    {
        ExpandWidgetFromHost();

        EnsureCollapsedContentViewAttached();
    }

    internal Task RevealItemAsync(string? itemId)
    {
        PrepareSurfaceForExternalInteraction();
        return _contentHost.CurrentContent is QuickCaptureWidgetContentAdapter quickCapture
            ? quickCapture.RevealItemAsync(itemId)
            : Task.CompletedTask;
    }



    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // A feature member inside a group is only the current Surface page.
        // Closing hides the group and must not disable the global feature.
        if (App.Current.WidgetManager?.GetWidgetGroupPresentation(_config.Id) is not null)
        {
            HideWindow();
            return;
        }

        if (FeatureWidgetSettings.IsFeatureWidget(_config.WidgetKind) &&
            App.Current.WidgetManager is { } widgetManager)
        {
            ShowFlyoutWithInteraction(
                CreateFeatureWidgetCloseFlyout(widgetManager),
                ContentWidgetShell.CloseActionButton);
            return;
        }

        HideWindow();
    }

    private void MoreButton_Click(object sender, WidgetMenuRequestedEventArgs e)
    {
        ShowFlyoutWithInteraction(
            CreateMoreFlyout(),
            e.Anchor,
            e.PointerPosition);
    }

    private void PositionLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetPositionLocked(!_config.IsPositionLocked);
        ApplyLockActionIconState();
    }

    private void SizeLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetSizeLocked(!_config.IsSizeLocked);
        ApplyLockActionIconState();
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (IsTrayHideInputSuppressed || IsHideAnimationRunning)
        {
            e.Handled = true;
            return;
        }

        if (ContentWidgetShell.IsCollapsed)
        {
            ShowFlyoutWithInteraction(
                CreateMoreFlyout(),
                ContentWidgetShell,
                e.GetPosition(ContentWidgetShell));
        }
        else
        {
            ShowFlyoutWithInteraction(
                CreateMoreFlyout(),
                ContentWidgetShell.TitleBar,
                e.GetPosition(ContentWidgetShell.TitleBar));
        }
        e.Handled = true;
    }

    private void ContentWidgetShell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (IsTrayHideInputSuppressed || IsHideAnimationRunning)
        {
            e.Handled = true;
            return;
        }

        // Item-level context menus mark the event handled before it reaches the shell.
        // Any remaining right click is therefore on the content background and should
        // expose the same widget actions as the title bar.
        ShowFlyoutWithInteraction(
            CreateMoreFlyout(),
            ContentWidgetShell,
            e.GetPosition(ContentWidgetShell));
        e.Handled = true;
    }

    private void ContentWidgetShell_TitleDoubleTapped(object? sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsDragging || IsResizing || ContentWidgetShell.TitleEditorContent is not null)
            {
                return;
            }

            StartTitleRename();
        });
    }

    // ── Flyout ─────────────────────────────────────────────────

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();
        bool startRenameWhenClosed = false;
        bool showCloseWhenClosed = false;
        IDisposable? closeWidgetFlyoutHandoff = null;
        bool showForegroundColorPickerWhenClosed = false;
        bool showTitleIconCustomizerWhenClosed = false;
        bool showBackgroundCustomizerWhenClosed = false;
        bool showTodoExportWhenClosed = false;
        TodoWidgetContentAdapter? pendingTodoExportAdapter = null;
        IDisposable? pickerHandoff = null;

        if (CurrentContent is ClipboardWidgetContentAdapter clipboardContent)
        {
            PopulateClipboardMoreMenuItems(flyout, clipboardContent);
        }
        else if (CurrentContent is TodoWidgetContentAdapter todoContent)
        {
            var exportTodoItem = new MenuFlyoutItem
            {
                Text = App.Current.LocalizationService.T("Todo.Export.MenuItem"),
                Icon = new FontIcon { Glyph = "\uEDE1" }
            };
            exportTodoItem.Click += (_, _) =>
            {
                showTodoExportWhenClosed = true;
                pendingTodoExportAdapter = todoContent;
                pickerHandoff ??= AcquireCloseWidgetFlyoutHandoff();
            };
            flyout.Items.Add(exportTodoItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        else if (CurrentContent is GlanceWidgetContentAdapter glance)
        {
            GlanceWidgetContextMenuBuilder.Append(
                flyout,
                glance.ViewModel,
                App.Current.LocalizationService);
        }
        else if (CurrentContent is CustomWidgetContentAdapter customContent)
        {
            CustomWidgetContextMenuBuilder.PopulateMenuItems(
                flyout,
                customContent,
                App.Current.LocalizationService,
                section => App.Current.ShowSettings(section),
                widgetId => App.Current.ShowCustomWidgetStudio(widgetId));
        }

        var rename = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        rename.Click += (_, _) => startRenameWhenClosed = true;
        flyout.Closed += (_, _) =>
        {
            try
            {
                if (startRenameWhenClosed)
                {
                    DispatcherQueue.TryEnqueue(StartTitleRename);
                }
                else if (showCloseWhenClosed)
                {
                    QueueCloseWidgetFlyout(closeWidgetFlyoutHandoff);
                    closeWidgetFlyoutHandoff = null;
                }
                else if (showForegroundColorPickerWhenClosed)
                {
                    QueueInteractionGuardedFlyout(
                        pickerHandoff,
                        () =>
                        {
                            ShowFlyoutWithInteraction(
                                BuildWidgetForegroundColorPickerFlyout(),
                                ContentWidgetShell);
                            return Task.CompletedTask;
                        });
                    pickerHandoff = null;
                }
                else if (showTitleIconCustomizerWhenClosed)
                {
                    QueueInteractionGuardedFlyout(
                        pickerHandoff,
                        () =>
                        {
                            ShowTitleIconCustomizer();
                            return Task.CompletedTask;
                        });
                    pickerHandoff = null;
                }
                else if (showBackgroundCustomizerWhenClosed)
                {
                    QueueInteractionGuardedFlyout(
                        pickerHandoff,
                        () =>
                        {
                            ShowWidgetBackgroundCustomizer();
                            return Task.CompletedTask;
                        });
                    pickerHandoff = null;
                }
                else if (showTodoExportWhenClosed && pendingTodoExportAdapter is { } exportAdapter)
                {
                    QueueInteractionGuardedFlyout(
                        pickerHandoff,
                        () => ShowTodoExportFlyoutAsync(exportAdapter));
                    pickerHandoff = null;
                }
            }
            finally
            {
                // The color toggle keeps this menu open (#437), so rename,
                // disable, or settings can still win the dismissal after the
                // picker handoff was armed. A handoff its branch did not
                // queue must release here, or the compact lease leaks and a
                // Smart capsule stays expanded forever.
                pickerHandoff?.Dispose();
                closeWidgetFlyoutHandoff?.Dispose();
            }
        };
        flyout.Items.Add(rename);

        // All per-widget appearance entries live under one top-level submenu
        // (Windows 11 keeps primary context commands few and grouped).
        // WidgetCustomIconAndBackgroundLiveUnderStyleMenu: the icon and
        // background customizers and their flyout handoffs are children of
        // this submenu, not top-level items.
        var styleMenu = new MenuFlyoutSubItem
        {
            Text = App.Current.LocalizationService.T("Widget.StyleMenu.Label"),
            Icon = new FontIcon { Glyph = "\uE790" }
        };

        var customizeIcon = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Widget.CustomIcon.MenuLabel"),
            Icon = new FontIcon { Glyph = "\uE890" }
        };
        customizeIcon.Click += (_, _) =>
        {
            showTitleIconCustomizerWhenClosed = true;
            // Same transition gap as the color picker chain: hold the
            // interaction until the customizer flyout takes over.
            pickerHandoff ??= AcquireCloseWidgetFlyoutHandoff();
        };
        styleMenu.Items.Add(customizeIcon);

        var customizeBackground = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Widget.CustomBackground.MenuLabel"),
            Icon = new FontIcon { Glyph = "\uE91B" }
        };
        customizeBackground.Click += (_, _) =>
        {
            showBackgroundCustomizerWhenClosed = true;
            // Same handoff chain as the icon customizer above.
            pickerHandoff ??= AcquireCloseWidgetFlyoutHandoff();
        };
        styleMenu.Items.Add(customizeBackground);

        styleMenu.Items.Add(WidgetBorderMenuBuilder.Create(
            App.Current.LocalizationService,
            _config,
            SetWidgetBorderModeOverride));

        styleMenu.Items.Add(WidgetChromeMenuBuilder.Create(
            _config,
            _descriptor,
            App.Current.LocalizationService,
            App.Current.WidgetManager,
            SetChromeModeOverride));

        styleMenu.Items.Add(WidgetForegroundMenuBuilder.Create(
            _config,
            App.Current.LocalizationService,
            SetWidgetForegroundModeOverride,
            () =>
            {
                showForegroundColorPickerWhenClosed = true;
                // Hold the interaction across the flyout transition like the
                // close confirmation chain: without the handoff a Smart
                // capsule collapses between the menu closing and the color
                // picker opening.
                pickerHandoff ??= AcquireCloseWidgetFlyoutHandoff();
            }));
        flyout.Items.Add(styleMenu);

        flyout.Items.Add(WidgetCollapseMenuBuilder.Create(
            _config,
            SettingsService.Settings.WidgetCollapseBehavior,
            SettingsService.Settings.WidgetCompactExpansionDirection,
            App.Current.LocalizationService,
            SetCollapseBehaviorOverride,
            SetCompactExpansionDirectionOverride,
            ResetCompactWidthOverride));
        flyout.Items.Add(WidgetLockMenuBuilder.Create(
            App.Current.LocalizationService,
            _config.IsPositionLocked,
            _config.IsSizeLocked,
            SetPositionLocked,
            SetSizeLocked));

        // Hidden entirely on single-monitor systems, matching how Windows
        // hides commands that cannot apply; null must not reach Items.Add.
        if (WidgetScreenMenuBuilder.TryCreate(
                App.Current.LocalizationService,
                _config,
                ApplyScreenBinding) is { } screenMenu)
        {
            flyout.Items.Add(screenMenu);
        }

        if (_config.WidgetKind is WidgetKind.File)
        {
            flyout.Items.Add(
                FileWidgetFolderOpenBehaviorMenuBuilder.Create(
                    _config,
                    App.Current.LocalizationService,
                    SetFileWidgetFolderOpenBehaviorOverride));
        }

        WidgetGroupMenuBuilder.Append(
            flyout,
            _config,
            App.Current.WidgetManager,
            App.Current.LocalizationService);

        flyout.Items.Add(WidgetSettingsMenuHelper.CreateMenuItem(
            _config.WidgetKind,
            App.Current.LocalizationService,
            beforeClick: flyout.Hide,
            widgetId: _config.Id));

        flyout.Items.Add(new MenuFlyoutSeparator());
        var disableWidget = new MenuFlyoutItem
        {
            Text = GetFeatureWidgetCloseMenuText(),
            Icon = new FontIcon { Glyph = GetFeatureWidgetCloseMenuGlyph() }
        };
        WidgetDangerActionStyle.Apply(disableWidget);
        disableWidget.Click += (_, _) =>
        {
            showCloseWhenClosed = true;
            closeWidgetFlyoutHandoff ??=
                AcquireCloseWidgetFlyoutHandoff();
        };
        flyout.Items.Add(disableWidget);

        return flyout;
    }

    private void SetFileWidgetFolderOpenBehaviorOverride(
        string? behavior)
    {
        FileWidgetFolderOpenBehaviorNames.SetOverride(
            _config,
            behavior);
        SettingsService.UpdateWidget(_config);
        if (CurrentContent is FileWidgetContentAdapter fileSurface)
        {
            _ = fileSurface.ApplyFolderOpenBehaviorChangeAsync();
        }
    }

    private void ProvideWidgetActionsForContentMenu(
        WidgetHostContextMenuOpeningEventArgs e)
    {
        e.TitleStyleItem = WidgetChromeMenuBuilder.Create(
            _config,
            _descriptor,
            App.Current.LocalizationService,
            App.Current.WidgetManager,
            SetChromeModeOverride);

        bool showCloseWhenClosed = false;
        IDisposable? closeWidgetFlyoutHandoff = null;
        var closeWidget = new MenuFlyoutItem
        {
            Text = GetFeatureWidgetCloseMenuText(),
            Icon = new FontIcon { Glyph = GetFeatureWidgetCloseMenuGlyph() }
        };
        WidgetDangerActionStyle.Apply(closeWidget);
        closeWidget.Click += (_, _) =>
        {
            showCloseWhenClosed = true;
            closeWidgetFlyoutHandoff ??=
                AcquireCloseWidgetFlyoutHandoff();
        };
        e.Menu.Closed += (_, _) =>
        {
            if (showCloseWhenClosed)
            {
                QueueCloseWidgetFlyout(closeWidgetFlyoutHandoff);
                closeWidgetFlyoutHandoff = null;
            }
        };
        e.CloseWidgetItem = closeWidget;
    }

    private string GetFeatureWidgetCloseMenuGlyph() =>
        _config.WidgetKind == WidgetKind.Custom ? "\uE74D" : "\uE7E8";

    private string GetFeatureWidgetCloseMenuText()
    {
        LocalizationService localization = App.Current.LocalizationService;
        return _config.WidgetKind switch
        {
            WidgetKind.Custom => localization.Format(
                "CustomWidget.Menu.DeleteWidget",
                _config.Name),
            WidgetKind.Glance => localization.Format(
                "Widget.FeatureWidget.DisableConfirmTitle",
                _config.Name),
            _ => localization.T("Widget.FeatureWidget.Disable")
        };
    }

    private Task ShowCloseWidgetFlyout(FrameworkElement target)
    {
        if (_isCloseWidgetPending ||
            App.Current.WidgetManager is not { } widgetManager)
        {
            return Task.CompletedTask;
        }

        return _config.WidgetKind == WidgetKind.File
            ? ShowFileWidgetCloseFlowAsync(widgetManager, target)
            : ShowFeatureWidgetCloseFlow(widgetManager, target);
    }

    private Task ShowFeatureWidgetCloseFlow(
        WidgetManager widgetManager,
        FrameworkElement target)
    {
        ShowFlyoutWithInteraction(
            CreateFeatureWidgetCloseFlyout(widgetManager),
            target);
        return Task.CompletedTask;
    }

    private async Task ShowFileWidgetCloseFlowAsync(
        WidgetManager widgetManager,
        FrameworkElement target)
    {
        // An empty managed folder has nothing to keep or move back, so skip
        // the close-mode selection and recycle the folder together with the
        // widget. The policy fails closed: an unknown entry count keeps the
        // selection flow, and the actual deletion still goes through the
        // recycle bin plus the manager's managed-root re-validation.
        bool canCleanupManagedStorage =
            widgetManager.CanCleanupManagedStorageForWidget(_config.Id);
        int? managedFolderEntryCount = canCleanupManagedStorage
            ? await CountTopLevelFolderEntriesAsync(
                Path.GetFullPath(_config.MappedFolderPath!))
            : null;
        if (FileWidgetCloseFlowPolicy.ShouldCloseEmptyManagedFolderDirectly(
                canCleanupManagedStorage,
                managedFolderEntryCount))
        {
            await ExecuteFileWidgetCloseActionAsync(
                WidgetRemovalAction.DeleteManagedFolder);
            return;
        }

        ShowFlyoutWithInteraction(
            CreateFileWidgetCloseFlyout(widgetManager),
            target);
    }

    private IDisposable AcquireCloseWidgetFlyoutHandoff()
    {
        BeginCompactInteraction();
        WidgetManager? widgetManager = App.Current.WidgetManager;
        widgetManager?.BeginWidgetInteraction(
            "content-close-confirmation-handoff");
        return new CloseWidgetFlyoutHandoff(this, widgetManager);
    }

    private void QueueCloseWidgetFlyout(IDisposable? interactionHandoff)
    {
        QueueInteractionGuardedFlyout(
            interactionHandoff,
            () => ShowCloseWidgetFlyout(ContentWidgetShell));
    }

    private void QueueInteractionGuardedFlyout(
        IDisposable? interactionHandoff,
        Func<Task> showAsync)
    {
        if (DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                // The show delegate acquires its own interaction (see
                // ShowFlyoutWithInteraction) before this handoff is released,
                // so a grouped Smart capsule cannot collapse between the two
                // MenuFlyouts.
                await showAsync();
            }
            catch (Exception ex)
            {
                App.Log($"[ContentWidget] Queued confirmation flyout failed: {ex}");
            }
            finally
            {
                interactionHandoff?.Dispose();
            }
        }))
        {
            return;
        }

        interactionHandoff?.Dispose();
    }

    private MenuFlyout CreateFeatureWidgetCloseFlyout(
        WidgetManager widgetManager)
    {
        LocalizationService localization = App.Current.LocalizationService;
        bool isCustomDelete = _config.WidgetKind == WidgetKind.Custom;
        string confirmTitle = isCustomDelete
            ? localization.Format("CustomWidget.DeleteConfirm.Title", _config.Name)
            : localization.Format("Widget.FeatureWidget.DisableConfirmTitle", _config.Name);
        string confirmActionText = isCustomDelete
            ? localization.T("Common.Delete")
            : localization.T("Widget.FeatureWidget.Disable");
        string confirmNote = isCustomDelete
            ? localization.T("CustomWidget.DeleteConfirm.Note")
            : localization.T("Widget.FeatureWidget.DisableConfirmNote");

        return WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
            new WidgetCompactConfirmationOptions(
                confirmTitle,
                confirmActionText,
                async () =>
                {
                    if (_isCloseWidgetPending)
                    {
                        return;
                    }

                    _isCloseWidgetPending = true;
                    try
                    {
                        await DisableCurrentFeatureWidgetAsync(widgetManager);
                    }
                    finally
                    {
                        _isCloseWidgetPending = false;
                    }
                })
            {
                Message = confirmNote,
                MessageGlyph = isCustomDelete ? "\uE74D" : "\uE946",
                CancelText = localization.T("Common.Cancel")
            });
    }

    private Task DisableCurrentFeatureWidgetAsync(WidgetManager widgetManager)
    {
        return _config.WidgetKind switch
        {
            WidgetKind.Glance => widgetManager.SetGlanceWidgetInstanceEnabledAsync(_config.Id, enabled: false),
            WidgetKind.Custom => widgetManager.RemoveCustomWidgetAsync(_config.Id),
            _ => widgetManager.SetFeatureWidgetEnabledAsync(
                _config.WidgetKind,
                enabled: false,
                reveal: false)
        };
    }

    private MenuFlyout CreateFileWidgetCloseFlyout(
        WidgetManager widgetManager)
    {
        LocalizationService localization = App.Current.LocalizationService;
        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = localization.Format(
                "Widget.DeleteWidgetTitle",
                _config.Name),
            Icon = new FontIcon { Glyph = "\uE74D" },
            IsEnabled = false
        });
        flyout.Items.Add(new MenuFlyoutSeparator());

        if (!widgetManager.CanCleanupManagedStorageForWidget(_config.Id))
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = localization.T("Widget.DeleteWidgetNote"),
                Icon = new FontIcon { Glyph = "\uE946" },
                IsEnabled = false
            });
            flyout.Items.Add(CreateFileWidgetCloseAction(
                localization.T("Widget.DeleteWidgetConfirm"),
                WidgetRemovalAction.RemoveWidgetOnly));
            flyout.Items.Add(CreateFileWidgetCloseCancel(flyout));
            return flyout;
        }

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = localization.T("Widget.DeleteManagedInfo"),
            Icon = new FontIcon { Glyph = "\uE897" },
            IsEnabled = false
        });
        flyout.Items.Add(CreateFileWidgetCloseAction(
            localization.T("Widget.KeepManagedFolder"),
            WidgetRemovalAction.RemoveWidgetOnly,
            "\uE8B7",
            isDanger: false));
        flyout.Items.Add(CreateFileWidgetCloseAction(
            localization.T("Widget.MoveBackThenDeleteFolder"),
            WidgetRemovalAction.MoveManagedFolderContentsToDesktop,
            "\uE8CA",
            isDanger: false));
        bool confirmFolderRecycleWhenClosed = false;
        IDisposable? recycleConfirmationHandoff = null;
        var recycleFolder = new MenuFlyoutItem
        {
            Text = localization.T("Widget.DeleteFolderToRecycleBin"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        WidgetDangerActionStyle.Apply(recycleFolder);
        recycleFolder.Click += (_, _) =>
        {
            confirmFolderRecycleWhenClosed = true;
            // Hold the interaction across the flyout transition: without the
            // handoff a Smart capsule collapses as soon as this flyout closes,
            // and the recycle confirmation below never becomes visible.
            recycleConfirmationHandoff ??= AcquireCloseWidgetFlyoutHandoff();
        };
        flyout.Items.Add(recycleFolder);
        flyout.Closed += (_, _) =>
        {
            if (confirmFolderRecycleWhenClosed)
            {
                QueueInteractionGuardedFlyout(
                    recycleConfirmationHandoff,
                    () => ShowDeleteManagedFolderConfirmationAsync());
                recycleConfirmationHandoff = null;
            }
        };
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateFileWidgetCloseCancel(flyout));
        return flyout;
    }

    private MenuFlyoutItem CreateFileWidgetCloseAction(
        string text,
        WidgetRemovalAction removalAction,
        string glyph = "\uE74D",
        bool isDanger = true)
    {
        var icon = new FontIcon { Glyph = glyph };
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = icon
        };
        if (isDanger)
        {
            WidgetDangerActionStyle.Apply(item);
        }
        item.Click += async (_, _) =>
            await ExecuteFileWidgetCloseActionAsync(removalAction);
        return item;
    }

    private async Task ShowDeleteManagedFolderConfirmationAsync()
    {
        if (_isCloseWidgetPending ||
            string.IsNullOrWhiteSpace(_config.MappedFolderPath) ||
            RootGrid.XamlRoot is null)
        {
            return;
        }

        string folderPath = Path.GetFullPath(_config.MappedFolderPath);
        string folderName = Path.GetFileName(folderPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = _config.Name;
        }

        // The confirmation flyout cannot soft-wrap, so a full path tail-truncates
        // and hides the folder name; the message shows parent\name instead and
        // falls back to the bare name when even that exceeds the widest
        // single-line CJK budget.
        string parentName = Path.GetFileName(
            Path.GetDirectoryName(folderPath.TrimEnd('\\', '/')) ?? string.Empty);
        string displayPath = string.IsNullOrEmpty(parentName)
            ? folderName
            : $"{parentName}\\{folderName}";
        if (displayPath.Length > 48)
        {
            displayPath = folderName;
        }

        int? itemCount = await CountTopLevelFolderEntriesAsync(folderPath);
        LocalizationService localization = App.Current.LocalizationService;
        string message = itemCount is int count
            ? localization.Format(
                "Widget.DeleteManagedFolderConfirmMessage",
                count,
                displayPath)
            : localization.Format(
                "Widget.DeleteManagedFolderConfirmMessageUnknownCount",
                displayPath);
        MenuFlyout confirmation =
            WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
            new WidgetCompactConfirmationOptions(
                localization.Format(
                "Widget.DeleteManagedFolderConfirmTitle",
                folderName),
                localization.T("Widget.MoveToRecycleBin"),
                async () => await ExecuteFileWidgetCloseActionAsync(
                    WidgetRemovalAction.DeleteManagedFolder))
            {
                Message = message,
                MessageGlyph = "\uE946",
                CancelText = localization.T("Common.Cancel"),
                CancelFirst = true
            });
        ShowFlyoutWithInteraction(confirmation, ContentWidgetShell);
    }

    private static Task<int?> CountTopLevelFolderEntriesAsync(
        string folderPath)
    {
        return Task.Run<int?>(() =>
        {
            try
            {
                return Directory.Exists(folderPath)
                    ? Directory.EnumerateFileSystemEntries(folderPath).Count()
                    : null;
            }
            catch
            {
                return null;
            }
        });
    }

    private async Task ExecuteFileWidgetCloseActionAsync(
        WidgetRemovalAction removalAction)
    {
        if (_isCloseWidgetPending ||
            App.Current.WidgetManager is not { } widgetManager)
        {
            return;
        }

        _isCloseWidgetPending = true;
        try
        {
            await widgetManager.RemoveWidgetAsync(
                _config.Id,
                removalAction,
                HWnd);
        }
        catch (Exception ex)
        {
            _isCloseWidgetPending = false;
            App.Log(
                $"[ContentWidget] Close widget failed id={_config.Id}: {ex}");
            // Same window-clipping problem as the title-rename failure: a
            // ContentDialog inside a small widget window gets cut off, so
            // surface the failure as in-widget feedback instead.
            ContentWidgetShell.ShowFeedback(new WidgetFeedbackRequest(
                App.Current.LocalizationService.Format(
                    "Widget.DeleteWidgetFailedWithReason",
                    ex.Message),
                WidgetFeedbackSeverity.Error,
                "widget-delete-error"));
        }
    }

    private MenuFlyoutItem CreateFileWidgetCloseCancel(MenuFlyout flyout)
    {
        var item = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Common.Cancel"),
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        item.Click += (_, _) => flyout.Hide();
        return item;
    }

    private void SetChromeModeOverride(WidgetChromeMode mode)
    {
        if (App.Current.WidgetManager is { } manager &&
            manager.IsWidgetGrouped(_config.Id))
        {
            if (manager.SetWidgetGroupChromeMode(_config, mode))
            {
                ApplyAppearancePreview();
            }
            return;
        }

        WidgetChromeModeNames.SetOverrideMode(_config, mode);
        SettingsService.UpdateWidget(_config);
        ApplyAppearancePreview();
    }

    private void SetWidgetBorderModeOverride(string? mode)
    {
        WidgetBorderCustomization.SetModeOverride(_config, mode);
        SettingsService.UpdateWidget(_config);
        ApplyAppearancePreview();
    }

    private static ToggleMenuFlyoutItem CreateToggleMenuItem(string text, string glyph, bool isChecked, Action<bool> applyValue)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph },
            IsChecked = isChecked
        };
        item.Click += (_, _) => applyValue(item.IsChecked);
        return item;
    }

    private void SetPositionLocked(bool value)
    {
        if (_config.IsPositionLocked == value)
        {
            return;
        }

        _config.IsPositionLocked = value;
        SettingsService.UpdateWidget(_config);
        SynchronizeWidgetGroupLayout();
        ApplyLockActionIconState();
    }

    private void ApplyScreenBinding(WidgetScreenBindingMode mode, string? boundScreenId)
    {
        if (_config.ScreenBindingMode == mode &&
            string.Equals(_config.BoundScreenId, boundScreenId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // SafeFireAndForget keeps the observed-and-log failure semantics of a
        // discarded task without letting an unobserved fault crash the app.
        if (App.Current?.WidgetManager is { } widgetManager)
        {
            App.SafeFireAndForget(
                () => widgetManager.ApplyScreenBindingAsync(_config.Id, mode, boundScreenId));
        }
    }

    private void SetSizeLocked(bool value)
    {
        if (_config.IsSizeLocked == value)
        {
            return;
        }

        _config.IsSizeLocked = value;
        SettingsService.UpdateWidget(_config);
        SynchronizeWidgetGroupLayout();
        ApplyLockActionIconState();
    }

    // ── Title rename ───────────────────────────────────────────

    private void StartTitleRename()
    {
        if (IsDragging ||
            IsResizing ||
            ContentWidgetShell.TitleEditorContent is not null)
        {
            return;
        }

        _isCancellingTitleRename = false;
        _titleRenameOpenedAtTick = Environment.TickCount64;
        BeginCompactInteraction();
        App.Current.WidgetManager?.BeginWidgetInteraction("content-title-rename-opened");
        var editor = CreateTitleRenameEditor();
        ContentWidgetShell.TitleEditorContent = editor;
        HoldTemporaryTopMost();
        ActivateForTitleRename();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(ContentWidgetShell.TitleEditorContent, editor))
            {
                ActivateForTitleRename();
            }
        });
        InlineEditorFocus.FocusWhenLoaded(
            editor,
            static focused => focused.SelectAll(),
            DispatcherQueue,
            "ContentTitleRename");
    }

    private void ActivateForTitleRename()
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            // Resting desktop-pinned widgets carry WS_EX_NOACTIVATE; strip it
            // before the explicit activation so the editor window can take
            // keyboard focus without waiting for the GotFocus routing.
            WidgetLayerService.PrepareForDesktopPinnedKeyboardInput(HWnd);
        }

        AppWindow.Show();
        base.Activate();
        Win32Helper.SetForegroundWindow(HWnd);
    }

    private TextBox CreateTitleRenameEditor()
    {
        var localization = App.Current.LocalizationService;
        double titleWidth = ContentWidgetShell.TitleTextElement.ActualWidth > 0
            ? ContentWidgetShell.TitleTextElement.ActualWidth + 36
            : (_titleViewModel.DisplayName.Length * 9.5) + 36;

        var editor = new TextBox
        {
            Text = _titleViewModel.DisplayName,
            PlaceholderText = localization.T("Widget.TitlePlaceholder"),
            Width = Math.Clamp(titleWidth, 120, 220),
            MaxWidth = 220,
            FontSize = Math.Max(ContentWidgetShell.TitleTextElement.FontSize - 1, 11),
            Style = GetTextBoxStyleResource("WidgetTitleRenameTextBoxStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        editor.KeyDown += TitleRenameEditor_KeyDown;
        editor.LostFocus += TitleRenameEditor_LostFocus;
        return editor;
    }

    private static Style? GetTextBoxStyleResource(string resourceKey)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Style style
            ? style
            : null;
    }

    private async void TitleRenameEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isCancellingTitleRename)
        {
            _isCancellingTitleRename = false;
            return;
        }

        if (InlineEditorFocus.TryRecoverFocusWithinGrace(
                _titleRenameOpenedAtTick,
                sender as TextBox))
        {
            return;
        }

        await CommitTitleRenameAsync();
    }

    private async void TitleRenameEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await CommitTitleRenameAsync();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelTitleRename();
            e.Handled = true;
        }
    }

    private async Task CommitTitleRenameAsync()
    {
        if (_isCommittingTitleRename ||
            ContentWidgetShell.TitleEditorContent is not TextBox editor)
        {
            return;
        }

        string newName = editor.Text.Trim();
        _isCommittingTitleRename = true;
        try
        {
            if (!string.IsNullOrEmpty(newName))
            {
                await App.Current.WidgetManager!.RenameWidgetAsync(_config.Id, newName);
                _titleViewModel.RefreshDisplayName();
            }

            CompleteTitleRename("content-title-rename-committed");
        }
        catch (Exception ex)
        {
            // A ContentDialog rendered inside a small widget window is
            // clipped by the window bounds. Surface the failure through the
            // shell's in-widget feedback instead, like the quick capture
            // rename does, and keep the editor open so the name can be fixed.
            App.Log(
                $"[ContentWidget] Title rename failed id={_config.Id}: {ex}");
            ContentWidgetShell.ShowFeedback(new WidgetFeedbackRequest(
                App.Current.LocalizationService.Format(
                    "Widget.RenameFailedWithReason",
                    ex.Message),
                WidgetFeedbackSeverity.Error,
                "widget-title-rename-error"));
            InlineEditorFocus.FocusWhenLoaded(
                editor,
                static focused => focused.SelectAll(),
                DispatcherQueue,
                "ContentTitleRename");
        }
        finally
        {
            _isCommittingTitleRename = false;
        }
    }

    private void CancelTitleRename()
    {
        _isCancellingTitleRename = true;
        CompleteTitleRename("content-title-rename-canceled");
    }

    private void CompleteTitleRename(string reason)
    {
        if (ContentWidgetShell.TitleEditorContent is TextBox editor)
        {
            editor.KeyDown -= TitleRenameEditor_KeyDown;
            editor.LostFocus -= TitleRenameEditor_LostFocus;
        }

        ContentWidgetShell.TitleEditorContent = null;
        EndCompactInteraction();
        App.Current.WidgetManager?.EndWidgetInteraction(reason);
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            return;
        }

        RestoreDesktopLayerFromManager();
    }

    private void ShowTitleIconCustomizer()
    {
        ShowFlyoutWithInteraction(
            BuildTitleIconCustomizerFlyout(ShowTitleIconCustomizer),
            ContentWidgetShell);
    }

    private void ShowWidgetBackgroundCustomizer()
    {
        ShowFlyoutWithInteraction(
            BuildWidgetBackgroundCustomizerFlyout(ShowWidgetBackgroundCustomizer),
            ContentWidgetShell);
    }

    private void ShowFlyoutWithInteraction(FlyoutBase flyout, FrameworkElement target, Windows.Foundation.Point? position = null, FlyoutPlacementMode? placement = null)
    {
        BeginCompactInteraction();
        App.Current.WidgetManager?.BeginWidgetInteraction("content-flyout-opened");
        flyout.Closed += (_, _) =>
        {
            EndCompactInteraction();
            App.Current.WidgetManager?.EndWidgetInteraction("content-flyout-closed");
            if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
            {
                return;
            }

            RestoreDesktopLayerFromManager();
        };

        try
        {
            if (position is Windows.Foundation.Point point && placement is FlyoutPlacementMode placedMode)
            {
                flyout.ShowAt(
                    target,
                    new FlyoutShowOptions { Position = point, Placement = placedMode });
            }
            else if (position is Windows.Foundation.Point posOnly)
            {
                flyout.ShowAt(
                    target,
                    new FlyoutShowOptions { Position = posOnly });
            }
            else if (placement is FlyoutPlacementMode modeOnly)
            {
                flyout.ShowAt(
                    target,
                    new FlyoutShowOptions { Placement = modeOnly });
            }
            else
            {
                flyout.ShowAt(target);
            }
        }
        catch (Exception ex)
        {
            // ShowAt can throw after the interaction pair above was taken
            // (e.g. a torn-down XamlRoot during window closing). Without this
            // rollback the compact lease leaks and a Smart capsule stays
            // expanded forever; a failed show never raises Closed.
            EndCompactInteraction();
            App.Current.WidgetManager?.EndWidgetInteraction("content-flyout-show-failed");
            App.Log($"[ContentWidget] Flyout show failed: {ex}");
        }
    }

    private sealed class CloseWidgetFlyoutHandoff : IDisposable
    {
        private ContentWidgetWindow? _owner;
        private WidgetManager? _widgetManager;

        public CloseWidgetFlyoutHandoff(
            ContentWidgetWindow owner,
            WidgetManager? widgetManager)
        {
            _owner = owner;
            _widgetManager = widgetManager;
        }

        public void Dispose()
        {
            ContentWidgetWindow? owner = Interlocked.Exchange(ref _owner, null);
            WidgetManager? widgetManager = Interlocked.Exchange(
                ref _widgetManager,
                null);
            if (owner is null)
            {
                return;
            }

            owner.EndCompactInteraction();
            widgetManager?.EndWidgetInteraction(
                "content-close-confirmation-handoff-completed");
        }
    }

    private void PopulateClipboardMoreMenuItems(
        MenuFlyout flyout,
        ClipboardWidgetContentAdapter clipboardContent)
    {
        var localization = App.Current.LocalizationService;
        bool isPaused = clipboardContent.ViewModel.IsPaused;

        var pauseItem = new MenuFlyoutItem
        {
            Text = localization.T(isPaused ? "Clipboard.Action.ResumeCapture" : "Clipboard.Action.PauseCapture"),
            Icon = new FontIcon { Glyph = isPaused ? "\uE768" : "\uE769" }
        };
        pauseItem.Click += (_, _) =>
        {
            flyout.Hide();
            clipboardContent.ViewModel.TogglePause();
        };
        flyout.Items.Add(pauseItem);

        var clearItem = new MenuFlyoutItem
        {
            Text = localization.T("Clipboard.Action.ClearUnpinned"),
            Icon = new FontIcon { Glyph = "\uE74D" },
            IsEnabled = clipboardContent.ViewModel.TotalItemCount > 0
        };
        clearItem.Click += async (_, _) =>
        {
            flyout.Hide();
            int removed = await clipboardContent.ViewModel.ClearUnpinnedAsync();
            if (removed > 0)
            {
                ContentWidgetShell.ShowFeedback(new WidgetFeedbackRequest(
                    localization.Format("Clipboard.Feedback.ClearedCount", removed),
                    WidgetFeedbackSeverity.Info,
                    "clipboard-cleared"));
            }
        };
        flyout.Items.Add(clearItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    private async Task ShowTodoExportFlyoutAsync(TodoWidgetContentAdapter todoContent)
    {
        LocalizationService loc = App.Current.LocalizationService;
        IReadOnlyList<TodoItem> items;
        TodoExportStatusScope initialScope;
        try
        {
            (items, initialScope) = await todoContent.PrepareExportSnapshotAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[TodoExport] Failed to snapshot Todo items: {ex}");
            ContentWidgetShell.ShowFeedback(new WidgetFeedbackRequest(
                loc.Format("Todo.Export.Status.Failed", ex.Message),
                WidgetFeedbackSeverity.Error,
                "todo-export-failed"));
            return;
        }

        if (items.Count == 0)
        {
            ContentWidgetShell.ShowFeedback(new WidgetFeedbackRequest(
                loc.T("Todo.Export.NoTasksInStore"),
                WidgetFeedbackSeverity.Info,
                "todo-export-empty"));
            return;
        }

        var panel = new DeskBox.Controls.Todo.TodoExportPanel(
            items,
            loc,
            initialScope,
            isSelectionSubset: false,
            showFlyoutChrome: true);

        Flyout exportFlyout = DeskBox.Controls.Todo.TodoExportPanel.CreateAnchoredFlyout(
            panel,
            FlyoutPlacementMode.BottomEdgeAlignedRight);

        bool exportStarted = false;
        panel.CancelRequested += (_, _) => exportFlyout.Hide();
        panel.ExportRequested += async (_, _) =>
        {
            if (exportStarted || !panel.CanExport)
            {
                return;
            }

            exportStarted = true;
            TodoExportOptions chosenOptions = panel.CurrentOptions;
            TodoItem[] itemsToExport = panel.CurrentPreview.Items.ToArray();
            IDisposable pickerLease = AcquireCloseWidgetFlyoutHandoff();
            try
            {
                exportFlyout.Hide();
                string? savedPath = await DeskBox.Controls.Todo.TodoExportWorkflow.PickAndExportAsync(
                    HWnd,
                    itemsToExport,
                    chosenOptions,
                    loc);

                if (!string.IsNullOrWhiteSpace(savedPath))
                {
                    ContentWidgetShell.ShowFeedback(new WidgetFeedbackRequest(
                        loc.Format("Todo.Export.Status.Exported", itemsToExport.Length),
                        WidgetFeedbackSeverity.Success,
                        "todo-export-succeeded",
                        loc.T("Todo.Export.Action.OpenFile"),
                        () =>
                        {
                            DeskBox.Controls.Todo.TodoExportWorkflow.OpenExportedFile(savedPath);
                            return Task.CompletedTask;
                        }));
                }
            }
            catch (Exception ex)
            {
                App.Log($"[TodoExport] Widget export failed: {ex}");
                ContentWidgetShell.ShowFeedback(new WidgetFeedbackRequest(
                    loc.Format("Todo.Export.Status.Failed", ex.Message),
                    WidgetFeedbackSeverity.Error,
                    "todo-export-failed"));
            }
            finally
            {
                pickerLease.Dispose();
            }
        };

        if (ContentWidgetShell.IsCollapsed)
        {
            ShowFlyoutWithInteraction(
                exportFlyout,
                ContentWidgetShell,
                placement: FlyoutPlacementMode.BottomEdgeAlignedRight);
        }
        else if (ContentWidgetShell.TitleBar is { IsLoaded: true, Visibility: Visibility.Visible, ActualHeight: > 0 } titleBar)
        {
            ShowFlyoutWithInteraction(
                exportFlyout,
                titleBar,
                placement: FlyoutPlacementMode.BottomEdgeAlignedRight);
        }
        else
        {
            double rightX = Math.Max(16, ContentWidgetShell.ActualWidth - 16);
            ShowFlyoutWithInteraction(
                exportFlyout,
                ContentWidgetShell,
                position: new Windows.Foundation.Point(rightX, 24),
                placement: FlyoutPlacementMode.BottomEdgeAlignedRight);
        }
    }

    // ── Color helpers ──────────────────────────────────────────
}
