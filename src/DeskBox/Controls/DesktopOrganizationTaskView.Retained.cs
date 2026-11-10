using System.IO;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    private readonly List<DesktopOrganizationPreviewCard> _retainedCards = [];
    private readonly HashSet<string> _retainedSelection = new(StringComparer.OrdinalIgnoreCase);
    private int _trayIconGeneration;

    private void ReleaseRetainedCards()
    {
        _trayIconGeneration++;
        foreach (var card in _retainedCards) card.Dispose();
        _retainedCards.Clear();
        RetainedRows.Children.Clear();
        TrayItemsPanel.Children.Clear();
    }

    private void RenderExcludedItems(DesktopOrganizationPlan plan)
    {
        ReleaseRetainedCards();
        var retained = GetRetainedPreviewItems();
        var movableItems = retained.Where(CanIncludeRetainedItem).ToList();
        var lockedItems = retained.Where(item => !CanIncludeRetainedItem(item)).ToList();
        _retainedSelection.IntersectWith(movableItems.Select(item => item.SourcePath));

        StayOnDesktopHeaderTitleText.Text = T("DesktopOrganization.Layout.KeptTitle");
        StayOnDesktopTrayTitleText.Text = Format("DesktopOrganization.Preview.ItemCount", retained.Count);
        RestoreAllExcludedButton.Content = T("DesktopOrganization.Layout.Include");
        RestoreAllExcludedButton.Visibility = !_hasCompletedExecution && _excludedSourcePaths.Count > 0
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

        RetainedEmptyText.Text = T("DesktopOrganization.Layout.NoRetained");
        RetainedEmptyCard.Visibility = retained.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            appSettings.ShowFileExtensions,
            appSettings.HideShortcutExtensionWhenShowingFileExtensions);

        var nameText = new TextBlock
        {
            Text = displayName,
            FontSize = 12,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = canInclude ? primaryBrush : secondaryBrush
        };

        var backText = new TextBlock
        {
            Text = T("DesktopOrganization.Layout.Include"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Foreground = accentBrush,
            Visibility = canInclude ? Visibility.Visible : Visibility.Collapsed
        };

        var rowGrid = new Grid
        {
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            ColumnSpacing = 8,
            CornerRadius = new CornerRadius(6),
            Background = transparentBg,
            Opacity = canInclude ? 1.0 : 0.56,
            CanDrag = false
        };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(iconHost, 0);
        Grid.SetColumn(nameText, 1);
        Grid.SetColumn(backText, 2);
        rowGrid.Children.Add(iconHost);
        rowGrid.Children.Add(nameText);
        rowGrid.Children.Add(backText);

        string reason = ExclusionDescription(item);
        string tooltip = item.Name + "\n" + reason;
        ToolTipService.SetToolTip(rowGrid, tooltip);

        if (canInclude)
        {
            bool isRowPointerPressed = false;
            bool isRowDragging = false;
            Windows.Foundation.Point rowPressStart = default;

            rowGrid.PointerEntered += (_, _) =>
            {
                rowGrid.Background = hoverBg;
                backText.Opacity = 1;
            };
            rowGrid.PointerExited += (_, _) =>
            {
                rowGrid.Background = transparentBg;
                backText.Opacity = 0;
            };
            rowGrid.AddHandler(
                UIElement.PointerPressedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
                {
                    if (_hasCompletedExecution || !e.GetCurrentPoint(rowGrid).Properties.IsLeftButtonPressed)
                    {
                        return;
                    }

                    isRowPointerPressed = true;
                    isRowDragging = false;
                    rowPressStart = e.GetCurrentPoint(this).Position;
                }),
                handledEventsToo: true);
            rowGrid.AddHandler(
                UIElement.PointerMovedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
                {
                    if (!isRowPointerPressed)
                    {
                        return;
                    }

                    var pt = e.GetCurrentPoint(this);
                    if (!pt.Properties.IsLeftButtonPressed)
                    {
                        isRowPointerPressed = false;
                        isRowDragging = false;
                        return;
                    }

                    if (!isRowDragging)
                    {
                        double dx = pt.Position.X - rowPressStart.X;
                        double dy = pt.Position.Y - rowPressStart.Y;
                        if ((dx * dx) + (dy * dy) < 25)
                        {
                            return;
                        }

                        isRowDragging = true;
                        rowGrid.CapturePointer(e.Pointer);
                        DesktopOrganizationPreviewCard.ActiveDraggedSourcePath = item.SourcePath;
                        DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths = [item.SourcePath];
                        DesktopOrganizationPreviewCard.NotifyDragSessionState(true);
                    }

                    DesktopOrganizationPreviewCard.NotifyPointerDragMoved(rowGrid, e);
                }),
                handledEventsToo: true);
            rowGrid.AddHandler(
                UIElement.PointerReleasedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
                {
                    if (!isRowPointerPressed)
                    {
                        return;
                    }

                    bool wasDragging = isRowDragging;
                    isRowPointerPressed = false;
                    isRowDragging = false;
                    rowGrid.ReleasePointerCapture(e.Pointer);

                    if (wasDragging)
                    {
                        DesktopOrganizationPreviewCard.NotifyPointerDragReleased(rowGrid, e);
                        return;
                    }

                    RestoreSingleRetainedItem(item);
                }),
                handledEventsToo: true);
        }

        _ = LoadTrayIconAsync(
            image,
            fallback,
            item.SourcePath,
            generation,
            appSettings.HideShortcutArrowOverlay,
            appSettings.ShowImageFilesAsIcons);
        return rowGrid;
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
        if (_isScanning || _isExecuting || _hasCompletedExecution || _basePlan is null || _plan is null)
        {
            return;
        }

        _retainedSelection.Clear();
        _retainedSelection.Add(item.SourcePath);
        IncludeOptionalItems(includeAll: false);
    }

    private void RestoreAllExcludedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || _plan is null)
        {
            return;
        }

        _excludedSourcePaths.Clear();
        foreach (var selection in _targetSelections.Values)
        {
            selection.IsSelected = true;
        }

        RenderPlan(_plan);
    }

    private void StayOnDesktopTray_DragOver(object sender, DragEventArgs e)
    {
        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (_isScanning || _isExecuting || _hasCompletedExecution ||
            (DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths.Count == 0 &&
             string.IsNullOrWhiteSpace(DesktopOrganizationPreviewCard.ActiveDraggedSourcePath)))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = T("DesktopOrganization.Layout.KeptTitle");
        e.DragUIOverride.IsCaptionVisible = true;
        StayOnDesktopTrayBorder.BorderBrush =
            NeutralInteractionBrush.ResolveThemedResource("AccentFillColorDefaultBrush", this);
    }

    private void StayOnDesktopTray_DragLeave(object sender, DragEventArgs e)
    {
        StayOnDesktopTrayBorder.BorderBrush =
            NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this);
    }

    private async void StayOnDesktopTray_Drop(object sender, DragEventArgs e)
    {
        StayOnDesktopTrayBorder.BorderBrush =
            NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this);

        if (_isScanning || _isExecuting || _hasCompletedExecution || _plan is null ||
            DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        List<string> droppedPaths = DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths.Count > 0
            ? DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths.ToList()
            : !string.IsNullOrWhiteSpace(DesktopOrganizationPreviewCard.ActiveDraggedSourcePath)
                ? [DesktopOrganizationPreviewCard.ActiveDraggedSourcePath!]
                : [];

        if (droppedPaths.Count == 0 && e.DataView.Contains(StandardDataFormats.Text))
        {
            try
            {
                string raw = await e.DataView.GetTextAsync();
                droppedPaths = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            catch
            {
                droppedPaths = [];
            }
        }

        DesktopOrganizationPreviewCard.ActiveDraggedSourcePath = null;
        DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths = [];
        DesktopOrganizationPreviewCard.NotifyDragSessionState(false);

        if (droppedPaths.Count == 0)
        {
            return;
        }

        foreach (string path in droppedPaths)
        {
            _excludedSourcePaths.Add(path);
        }

        DesktopOrganizationPreviewCard.ClearActiveTileSelection();
        RenderPlan(_plan);
    }

    private string ExclusionDescription(DesktopOrganizationFileSnapshot item) =>
        _newPaths.Contains(item.SourcePath)
            ? T("DesktopOrganization.Layout.NewHelp")
            : T("DesktopOrganization.Exclusion." + item.ExclusionReason);

    private bool CanIncludeRetainedItem(DesktopOrganizationFileSnapshot item) =>
        !_hasCompletedExecution && (item.CanOptIn || item.ExclusionReason == DesktopOrganizationExclusionReason.UserChoice);

    private DesktopOrganizationFileSnapshot FindPreviewSnapshot(string path, string name, DesktopOrganizationSourceScope scope) =>
        (_lastExecutionPlan ?? _plan)?.SourceItems.FirstOrDefault(item =>
            string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase)) ??
        new DesktopOrganizationFileSnapshot(path, name, Path.GetExtension(path), 0, DateTime.MinValue,
            DesktopOrganizationCategoryIds.Other, null, DesktopOrganizationExclusionReason.Unavailable, SourceScope: scope);

    private void IncludeOptionalItems(bool includeAll)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || _basePlan is null || _plan is null)
        {
            return;
        }

        var chosen = GetRetainedPreviewItems()
            .Where(item => CanIncludeRetainedItem(item) &&
                (includeAll || _retainedSelection.Contains(item.SourcePath)))
            .ToList();
        if (chosen.Count == 0)
        {
            return;
        }

        try
        {
            var chosenPaths = chosen.Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in chosen)
            {
                if (item.CanOptIn) _optionalIncludedPaths.Add(item.SourcePath);
                _excludedSourcePaths.Remove(item.SourcePath);
                _newPaths.Remove(item.SourcePath);
            }

            foreach (var target in _plan.Targets.Where(target => target.Items.Any(item => chosenPaths.Contains(item.SourcePath))))
            {
                if (_targetSelections.TryGetValue(target.SourceBucketId, out var selection))
                {
                    selection.IsSelected = true;
                }
            }

            _plan = Coordinator.CreatePreviewPlanWithOptionalItems(
                _basePlan,
                _optionalIncludedPaths,
                _plan.IncludePersonalDesktop,
                _plan.IncludePublicDesktop);

            foreach (var target in _plan.Targets.Where(target => target.Items.Any(item => chosenPaths.Contains(item.SourcePath))))
            {
                if (_targetSelections.TryGetValue(target.SourceBucketId, out var selection))
                {
                    selection.IsSelected = true;
                }
            }

            _retainedSelection.Clear();
            RenderPlan(_plan);
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
}
