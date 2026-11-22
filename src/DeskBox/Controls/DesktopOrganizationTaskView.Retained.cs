using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    private int _trayIconGeneration;

    private void ReleaseRetainedCards()
    {
        _trayIconGeneration++;
        TrayItemsPanel.Children.Clear();
    }

    private void RenderExcludedItems(DesktopOrganizationPlan? explicitPlan = null)
    {
        ReleaseRetainedCards();
        DesktopOrganizationPlan? plan = explicitPlan ?? (_hasCompletedExecution ? _lastExecutionPlan : Session.EffectivePlan);
        var retained = GetRetainedPreviewItems(plan);
        var movableItems = retained.Where(CanIncludeRetainedItem).ToList();
        var lockedItems = retained.Where(item => !CanIncludeRetainedItem(item)).ToList();

        StayOnDesktopHeaderTitleText.Text = T("DesktopOrganization.Layout.KeptTitle");
        StayOnDesktopTrayTitleText.Text = Format("DesktopOrganization.Preview.ItemCount", retained.Count);
        RestoreAllExcludedButton.Content = T("DesktopOrganization.Layout.Include");
        RestoreAllExcludedButton.Visibility = !_hasCompletedExecution && Session.ExcludedSourcePaths.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        TrayEmptyHintText.Visibility = Visibility.Collapsed;

        int generation = _trayIconGeneration;
        AppSettings appSettings = App.Current.SettingsService.Settings;

        if (movableItems.Count == 0)
        {
            TrayItemsPanel.Children.Add(CreateLaneEmptyDropZone());
        }
        else
        {
            foreach (var item in movableItems)
            {
                TrayItemsPanel.Children.Add(CreateLaneRow(item, canInclude: true, generation, appSettings));
            }
        }

        if (lockedItems.Count > 0)
        {
            Brush dividerBrush = NeutralInteractionBrush.ResolveThemedResource("DividerStrokeColorDefaultBrush", this)
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x9E, 0x9E, 0x9E));
            TrayItemsPanel.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(8, 8, 8, 6),
                Background = dividerBrush
            });

            Brush tertiaryBrush = NeutralInteractionBrush.ResolveThemedResource("TextFillColorTertiaryBrush", this)
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0x9E, 0x9E, 0x9E));
            var groupHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(8, 2, 8, 4)
            };
            groupHeader.Children.Add(new FontIcon
            {
                Glyph = "\uE72E",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = tertiaryBrush
            });
            groupHeader.Children.Add(new TextBlock
            {
                Text = $"{T("DesktopOrganization.Layout.FixedTitle")} · {lockedItems.Count}",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = tertiaryBrush
            });
            TrayItemsPanel.Children.Add(groupHeader);

            foreach (var item in lockedItems)
            {
                TrayItemsPanel.Children.Add(CreateLaneRow(item, canInclude: false, generation, appSettings));
            }
        }
    }

    private UIElement CreateLaneEmptyDropZone()
    {
        Brush tertiaryBrush = NeutralInteractionBrush.ResolveThemedResource("TextFillColorTertiaryBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x80, 0x9E, 0x9E, 0x9E));
        Brush strokeBrush = NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x9E, 0x9E, 0x9E));

        var dashedRect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            RadiusX = 6,
            RadiusY = 6,
            Stroke = strokeBrush,
            StrokeThickness = 1.2,
            StrokeDashArray = [4, 3],
            IsHitTestVisible = false
        };

        var hintText = new TextBlock
        {
            Text = T("DesktopOrganization.Layout.NoRetained"),
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            Foreground = tertiaryBrush
        };

        var host = new Grid
        {
            MinHeight = 132,
            Margin = new Thickness(6, 4, 6, 6)
        };
        host.Children.Add(dashedRect);
        host.Children.Add(hintText);
        return host;
    }

    private UIElement CreateLaneRow(
        DesktopOrganizationFileSnapshot item,
        bool canInclude,
        int generation,
        AppSettings appSettings)
    {
        const double iconSize = 22;
        Brush primaryBrush = NeutralInteractionBrush.ResolveThemedResource("TextFillColorPrimaryBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0));
        Brush secondaryBrush = NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0x9E, 0x9E, 0x9E));
        Brush accentBrush = NeutralInteractionBrush.ResolveThemedResource("AccentTextFillColorPrimaryBrush", this)
            ?? primaryBrush;
        Brush hoverBg = NeutralInteractionBrush.ResolveThemedResource("SubtleFillColorSecondaryBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Brush transparentBg = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var image = new Image
        {
            Width = iconSize,
            Height = iconSize,
            Stretch = Stretch.Uniform
        };
        var fallback = new FontIcon
        {
            Glyph = item.IsDirectory ? "\uE8B8" : "\uE7C3",
            FontSize = 17,
            Width = iconSize,
            Height = iconSize,
            Foreground = secondaryBrush
        };
        var iconHost = new Grid
        {
            Width = iconSize,
            Height = iconSize,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconHost.Children.Add(image);
        iconHost.Children.Add(fallback);

        string displayName = FileService.GetDisplayName(
            item.SourcePath,
            item.IsDirectory,
            appSettings.FileWidget.ShowFileExtensions,
            appSettings.FileWidget.HideShortcutExtensionWhenShowingFileExtensions);

        var nameText = new TextBlock
        {
            Text = displayName,
            FontSize = 12,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = canInclude ? primaryBrush : secondaryBrush
        };

        Button? includeButton = null;
        if (canInclude)
        {
            includeButton = new Button
            {
                Content = new TextBlock
                {
                    Text = T("DesktopOrganization.Layout.Include"),
                    FontSize = 11.5,
                    Foreground = accentBrush
                },
                Background = transparentBg,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 2, 6, 2),
                MinHeight = 24,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0
            };
            AutomationProperties.SetName(includeButton, $"{T("DesktopOrganization.Layout.Include")} {displayName}");
            ToolTipService.SetToolTip(includeButton, T("DesktopOrganization.Layout.Include"));
            includeButton.Click += (_, _) => RestoreSingleRetainedItem(item);
        }

        var rowGrid = new Grid
        {
            Height = 32,
            Padding = new Thickness(8, 0, 6, 0),
            ColumnSpacing = 8,
            CornerRadius = new CornerRadius(6),
            Background = transparentBg,
            Opacity = canInclude ? 1.0 : 0.62,
            CanDrag = false
        };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(iconHost, 0);
        Grid.SetColumn(nameText, 1);
        rowGrid.Children.Add(iconHost);
        rowGrid.Children.Add(nameText);
        if (includeButton is not null)
        {
            Grid.SetColumn(includeButton, 2);
            rowGrid.Children.Add(includeButton);
        }

        string reason = ExclusionDescription(item);
        string tooltip = item.Name + "\n" + reason;
        ToolTipService.SetToolTip(rowGrid, tooltip);

        var rowHost = new ContentControl
        {
            Content = rowGrid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            ManipulationMode = canInclude
                ? ManipulationModes.TranslateX | ManipulationModes.TranslateY
                : ManipulationModes.System
        };
        AutomationProperties.SetName(rowHost, $"{displayName}, {reason}");

        bool isPointerOver = false;
        bool isRowFocused = false;
        bool isButtonFocused = false;
        void UpdateRowVisualState()
        {
            bool active = isPointerOver || isRowFocused || isButtonFocused;
            rowGrid.Background = active ? hoverBg : transparentBg;
            if (includeButton is not null)
            {
                includeButton.Opacity = active ? 1 : 0;
            }
        }

        rowHost.PointerEntered += (_, _) =>
        {
            isPointerOver = true;
            UpdateRowVisualState();
        };
        rowHost.PointerExited += (_, _) =>
        {
            isPointerOver = false;
            UpdateRowVisualState();
        };
        rowHost.GotFocus += (_, _) =>
        {
            isRowFocused = true;
            UpdateRowVisualState();
        };
        rowHost.LostFocus += (_, _) =>
        {
            isRowFocused = false;
            UpdateRowVisualState();
        };

        if (includeButton is not null)
        {
            includeButton.GotFocus += (_, _) =>
            {
                isButtonFocused = true;
                UpdateRowVisualState();
            };
            includeButton.LostFocus += (_, _) =>
            {
                isButtonFocused = false;
                UpdateRowVisualState();
            };
        }

        var contextMenu = new MenuFlyout();
        if (canInclude)
        {
            var includeMenuItem = new MenuFlyoutItem
            {
                Text = T("DesktopOrganization.Layout.Include"),
                Icon = new FontIcon { Glyph = "\uE710" }
            };
            includeMenuItem.Click += (_, _) => RestoreSingleRetainedItem(item);
            contextMenu.Items.Add(includeMenuItem);
        }

        var detailsMenuItem = new MenuFlyoutItem
        {
            Text = T("DesktopOrganization.Layout.Details"),
            Icon = new FontIcon { Glyph = "\uE946" }
        };
        detailsMenuItem.Click += (_, _) => ShowRetainedItemDetailsFlyout(rowHost, item, canInclude);
        contextMenu.Items.Add(detailsMenuItem);
        rowHost.ContextFlyout = contextMenu;

        if (canInclude)
        {
            rowHost.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler((_, e) =>
                {
                    if (_isScanning || _isExecuting || _hasCompletedExecution ||
                        IsOriginInsideButton(e.OriginalSource as DependencyObject, rowHost))
                    {
                        return;
                    }

                    _dragController.TryBeginCandidate(
                        rowHost,
                        e,
                        DesktopOrganizationDragOrigin.StayOnDesktopTray,
                        string.Empty,
                        () => [item.SourcePath],
                        onClickWithoutDrag: null);
                }),
                handledEventsToo: true);

            rowHost.DoubleTapped += (_, e) =>
            {
                if (!IsOriginInsideButton(e.OriginalSource as DependencyObject, rowHost))
                {
                    RestoreSingleRetainedItem(item);
                }
            };

            rowHost.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter && !IsOriginInsideButton(e.OriginalSource as DependencyObject, rowHost))
                {
                    e.Handled = true;
                    RestoreSingleRetainedItem(item);
                }
                else if (e.Key == VirtualKey.Space)
                {
                    e.Handled = true;
                    ShowRetainedItemDetailsFlyout(rowHost, item, canInclude: true);
                }
            };
        }
        else
        {
            rowHost.Tapped += (_, _) => ShowRetainedItemDetailsFlyout(rowHost, item, canInclude: false);
            rowHost.KeyDown += (_, e) =>
            {
                if (e.Key is VirtualKey.Enter or VirtualKey.Space)
                {
                    e.Handled = true;
                    ShowRetainedItemDetailsFlyout(rowHost, item, canInclude: false);
                }
            };
        }

        _ = LoadTrayIconAsync(
            image,
            fallback,
            item.SourcePath,
            generation,
            appSettings.FileWidget.HideShortcutArrowOverlay,
            appSettings.FileWidget.ShowImageFilesAsIcons);
        return rowHost;
    }

    private void ShowRetainedItemDetailsFlyout(
        FrameworkElement target,
        DesktopOrganizationFileSnapshot item,
        bool canInclude)
    {
        Brush primaryBrush = NeutralInteractionBrush.ResolveThemedResource("TextFillColorPrimaryBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0));
        Brush secondaryBrush = NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0x9E, 0x9E, 0x9E));

        string scopeLabel = T(item.SourceScope == DesktopOrganizationSourceScope.Public
            ? "DesktopOrganization.Public.SharedLabel"
            : "DesktopOrganization.Public.PersonalLabel");

        var contentStack = new StackPanel
        {
            MaxWidth = 300,
            Spacing = 6
        };
        contentStack.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = primaryBrush
        });
        contentStack.Children.Add(new TextBlock
        {
            Text = $"{scopeLabel} · {ExclusionDescription(item)}",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = secondaryBrush
        });
        contentStack.Children.Add(new TextBlock
        {
            Text = item.SourcePath,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = secondaryBrush
        });

        var flyout = new Flyout { Content = contentStack };
        if (canInclude)
        {
            var actionButton = new Button
            {
                Content = T("DesktopOrganization.Layout.Include"),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            actionButton.Click += (_, _) =>
            {
                flyout.Hide();
                RestoreSingleRetainedItem(item);
            };
            contentStack.Children.Add(actionButton);
        }

        flyout.ShowAt(target, new FlyoutShowOptions { Placement = FlyoutPlacementMode.LeftEdgeAlignedTop });
    }

    private static bool IsOriginInsideButton(DependencyObject? origin, DependencyObject boundary)
    {
        DependencyObject? current = origin;
        while (current is not null && !ReferenceEquals(current, boundary))
        {
            if (current is ButtonBase)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async Task LoadTrayIconAsync(
        Image image,
        FontIcon fallback,
        string sourcePath,
        int generation,
        bool hideArrow,
        bool imagesAsIcons)
    {
        try
        {
            var icon = await IconHelper.GetIconAsync(
                sourcePath,
                hideShortcutArrowOverlay: hideArrow,
                showImageFilesAsIcons: imagesAsIcons,
                decodePixelWidth: 44,
                cacheScope: DesktopOrganizationPreviewCard.PreviewIconCacheScope);
            if (generation != _trayIconGeneration || icon is null)
            {
                return;
            }

            image.Source = icon;
            fallback.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Keep fallback icon.
        }
    }

    private void RestoreSingleRetainedItem(DesktopOrganizationFileSnapshot item)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || Session.BasePlan is null)
        {
            return;
        }

        _dragController.CancelActiveDrag();
        try
        {
            DesktopOrganizationWorkbenchSession.MoveResult moveResult = Session.IncludeItems([item.SourcePath]);
            if (moveResult.MovedCount > 0)
            {
                _pendingEmptyCustomBoxRenameId = null;
                RenderSessionPlan();
            }
        }
        catch (Exception ex)
        {
            App.Log("[DesktopOrganization] Optional preview failed: " + ex);
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = T("DesktopOrganization.Result.FailedBody");
            ResultInfo.IsOpen = true;
        }
    }

    private void RestoreAllExcludedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || Session.BasePlan is null)
        {
            return;
        }

        _dragController.CancelActiveDrag();
        if (Session.RestoreManualExclusions() > 0)
        {
            RenderSessionPlan();
        }
    }

    private string ExclusionDescription(DesktopOrganizationFileSnapshot item) =>
        Session.NewSourcePaths.Contains(item.SourcePath)
            ? T("DesktopOrganization.Layout.NewHelp")
            : T("DesktopOrganization.Exclusion." + item.ExclusionReason);

    private bool CanIncludeRetainedItem(DesktopOrganizationFileSnapshot item) =>
        !_hasCompletedExecution &&
        (item.IsEligible || item.CanOptIn || item.ExclusionReason == DesktopOrganizationExclusionReason.UserChoice);

    private DesktopOrganizationFileSnapshot FindPreviewSnapshot(string path, string name, DesktopOrganizationSourceScope scope) =>
        (_lastExecutionPlan ?? Session.BasePlan ?? Session.EffectivePlan)?.SourceItems.FirstOrDefault(item =>
            string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase)) ??
        new DesktopOrganizationFileSnapshot(path, name, System.IO.Path.GetExtension(path), 0, DateTime.MinValue,
            DesktopOrganizationCategoryIds.Other, null, DesktopOrganizationExclusionReason.Unavailable, SourceScope: scope);

    private IReadOnlyList<DesktopOrganizationFileSnapshot> GetRetainedPreviewItems(DesktopOrganizationPlan? plan = null)
    {
        DesktopOrganizationPlan? sourcePlan = plan ?? (_hasCompletedExecution ? _lastExecutionPlan : Session.EffectivePlan);
        if (sourcePlan is null)
        {
            return [];
        }

        return sourcePlan.ExcludedItems
            .Where(item => DesktopOrganizationPreviewSummary.IncludesSource(sourcePlan, item))
            .DistinctBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
