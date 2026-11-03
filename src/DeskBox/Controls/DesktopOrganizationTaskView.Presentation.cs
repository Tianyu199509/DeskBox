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
    private readonly List<DesktopOrganizationPreviewCard> _targetCards = [];
    private readonly List<DesktopOrganizationPreviewCard> _completedCards = [];
    private readonly Dictionary<string, DesktopOrganizationTargetSelection> _targetSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OrganizationHistoryItem> _completedItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OrganizationHistoryItem> _restoredItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _reassignedItemBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _customBoxTitles = new(StringComparer.Ordinal);
    private readonly List<(string BucketId, string DisplayName)> _customBoxes = [];
    private int _customBoxCounter;
    private bool _isPreviewReady;
    private FrameworkElement? _newBoxGhostCard;
    private Action<bool>? _setGhostBoxHighlight;

    internal bool HasBoxCustomizations =>
        _reassignedItemBuckets.Count > 0 || _customBoxTitles.Count > 0 || _customBoxes.Count > 0;

    internal void ClearBoxCustomizations()
    {
        _reassignedItemBuckets.Clear();
        _customBoxTitles.Clear();
        _customBoxes.Clear();
        _customBoxCounter = 0;
        DesktopOrganizationPreviewCard.ClearActiveTileSelection();
    }

    private void ReleasePreviewCards()
    {
        _isPreviewReady = false;
        ExecuteButton.IsEnabled = false;
        DesktopOrganizationPreviewCard.DragSessionStateChanged -= OnDragSessionStateChanged;
        DesktopOrganizationPreviewCard.PointerDragMoved -= OnPointerDragMoved;
        DesktopOrganizationPreviewCard.PointerDragReleased -= OnPointerDragReleased;
        PointerDragOverlayCanvas.Visibility = Visibility.Collapsed;
        _newBoxGhostCard = null;
        _setGhostBoxHighlight = null;
        foreach (var card in _targetCards)
        {
            card.Dispose();
        }
        _targetCards.Clear();
        TargetRows.Children.Clear();
    }

    private void OnDragSessionStateChanged(object? sender, bool isDragging)
    {
        if (!isDragging)
        {
            PointerDragOverlayCanvas.Visibility = Visibility.Collapsed;
            StayOnDesktopTrayBorder.BorderBrush =
                NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this);
            _setGhostBoxHighlight?.Invoke(false);
            foreach (var card in _targetCards)
            {
                card.SetDropTargetHighlight(false);
            }
        }

        if (_newBoxGhostCard is not null && !_hasCompletedExecution)
        {
            _newBoxGhostCard.Visibility = HasAnyActiveScopeItems(_plan)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private bool HasAnyActiveScopeItems(DesktopOrganizationPlan? plan)
    {
        if (plan is null || (!plan.IncludePersonalDesktop && !plan.IncludePublicDesktop))
        {
            return false;
        }

        return plan.SourceItems.Any(item => DesktopOrganizationPreviewSummary.IncludesSource(plan, item)) ||
               plan.Targets.Any(t => t.Items.Any(item => DesktopOrganizationPreviewSummary.IncludesSource(plan, item))) ||
               _customBoxes.Count > 0;
    }

    private void OnPointerDragMoved(object? sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution)
        {
            return;
        }

        IReadOnlyList<string> draggedPaths = DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths;
        if (draggedPaths.Count == 0 && !string.IsNullOrWhiteSpace(DesktopOrganizationPreviewCard.ActiveDraggedSourcePath))
        {
            draggedPaths = [DesktopOrganizationPreviewCard.ActiveDraggedSourcePath!];
        }

        if (draggedPaths.Count == 0)
        {
            PointerDragOverlayCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        Windows.Foundation.Point pt = e.GetCurrentPoint(this).Position;
        string itemSummary = draggedPaths.Count == 1
            ? Path.GetFileName(draggedPaths[0])
            : Format("DesktopOrganization.Layout.MoveCount", draggedPaths.Count);

        bool overTray = IsPointOverElement(pt, StayOnDesktopTrayBorder);
        bool overNewBox = !overTray && IsPointOverElement(pt, _newBoxGhostCard);
        DesktopOrganizationPreviewCard? hoveredCard = null;
        if (!overTray && !overNewBox)
        {
            hoveredCard = _targetCards.FirstOrDefault(c => IsPointOverElement(pt, c));
        }

        StayOnDesktopTrayBorder.BorderBrush = NeutralInteractionBrush.ResolveThemedResource(
            overTray ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush",
            this);
        _setGhostBoxHighlight?.Invoke(overNewBox);
        foreach (var card in _targetCards)
        {
            card.SetDropTargetHighlight(ReferenceEquals(card, hoveredCard));
        }

        string targetSuffix = overTray
            ? " → " + T("DesktopOrganization.Layout.KeptTitle")
            : overNewBox
                ? " → " + T("DesktopOrganization.Layout.RouteBadgeNew")
                : hoveredCard is not null
                    ? " → " + hoveredCard.EffectiveBoxTitle
                    : string.Empty;

        PointerDragBadgeText.Text = itemSummary + targetSuffix;
        PointerDragOverlayCanvas.Visibility = Visibility.Visible;
        double badgeX = Math.Clamp(pt.X + 14, 4, Math.Max(4, ActualWidth - 220));
        double badgeY = Math.Clamp(pt.Y + 14, 4, Math.Max(4, ActualHeight - 40));
        Canvas.SetLeft(PointerDragBadge, badgeX);
        Canvas.SetTop(PointerDragBadge, badgeY);
    }

    private void OnPointerDragReleased(object? sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        PointerDragOverlayCanvas.Visibility = Visibility.Collapsed;
        StayOnDesktopTrayBorder.BorderBrush =
            NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this);
        _setGhostBoxHighlight?.Invoke(false);
        foreach (var card in _targetCards)
        {
            card.SetDropTargetHighlight(false);
        }

        List<string> droppedPaths = DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths.Count > 0
            ? DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths.ToList()
            : !string.IsNullOrWhiteSpace(DesktopOrganizationPreviewCard.ActiveDraggedSourcePath)
                ? [DesktopOrganizationPreviewCard.ActiveDraggedSourcePath!]
                : [];

        DesktopOrganizationPreviewCard.ActiveDraggedSourcePath = null;
        DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths = [];
        DesktopOrganizationPreviewCard.NotifyDragSessionState(false);

        if (_isScanning || _isExecuting || _hasCompletedExecution || _plan is null || droppedPaths.Count == 0)
        {
            return;
        }

        Windows.Foundation.Point pt = e.GetCurrentPoint(this).Position;
        if (IsPointOverElement(pt, StayOnDesktopTrayBorder))
        {
            foreach (string path in droppedPaths)
            {
                _excludedSourcePaths.Add(path);
            }

            DesktopOrganizationPreviewCard.ClearActiveTileSelection();
            RenderPlan(_plan);
            return;
        }

        if (IsPointOverElement(pt, _newBoxGhostCard))
        {
            CreateNewBoxWithFiles(droppedPaths);
            return;
        }

        var targetCard = _targetCards.FirstOrDefault(c => IsPointOverElement(pt, c));
        if (targetCard is not null &&
            (sender is not DesktopOrganizationPreviewCard sourceCard ||
             !ReferenceEquals(sourceCard, targetCard)))
        {
            MoveItemsToBox(droppedPaths, targetCard.SourceBucketId);
        }
    }

    private bool IsPointOverElement(Windows.Foundation.Point ptInTaskView, FrameworkElement? element)
    {
        if (element is null ||
            element.Visibility != Visibility.Visible ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            GeneralTransform transform = TransformToVisual(element);
            Windows.Foundation.Point localPt = transform.TransformPoint(ptInTaskView);
            return localPt.X >= 0 &&
                   localPt.Y >= 0 &&
                   localPt.X <= element.ActualWidth &&
                   localPt.Y <= element.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyUserBoxCustomizations(DesktopOrganizationPlan plan)
    {
        foreach ((string bucketId, string displayName) in _customBoxes)
        {
            if (plan.Targets.All(t => !string.Equals(t.SourceBucketId, bucketId, StringComparison.Ordinal)))
            {
                string title = _customBoxTitles.GetValueOrDefault(bucketId, displayName);
                plan.Targets.Add(new DesktopOrganizationTargetPlan
                {
                    SourceBucketId = bucketId,
                    CategoryId = DesktopOrganizationCategoryIds.Other,
                    TargetWidgetId = bucketId,
                    SuggestedDisplayName = title,
                    TargetDirectoryPath = Path.Combine(plan.StorageRootPath, title),
                    CreatesWidget = true,
                    Items = []
                });
            }
        }

        if (_reassignedItemBuckets.Count > 0)
        {
            var allSnapshots = plan.Targets
                .SelectMany(t => t.Items)
                .Concat(plan.SourceItems)
                .GroupBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach ((string sourcePath, string targetBucketId) in _reassignedItemBuckets.ToList())
            {
                var destTarget = plan.Targets.FirstOrDefault(t =>
                    string.Equals(t.SourceBucketId, targetBucketId, StringComparison.Ordinal));
                if (destTarget is null || !allSnapshots.TryGetValue(sourcePath, out var snapshot))
                {
                    continue;
                }

                foreach (var target in plan.Targets)
                {
                    target.Items.RemoveAll(i =>
                        string.Equals(i.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
                }

                var eligibleSnapshot = snapshot with
                {
                    ExclusionReason = DesktopOrganizationExclusionReason.None
                };
                destTarget.Items.Add(eligibleSnapshot);
            }
        }

        foreach ((string bucketId, string customTitle) in _customBoxTitles)
        {
            int index = plan.Targets.FindIndex(t =>
                string.Equals(t.SourceBucketId, bucketId, StringComparison.Ordinal));
            if (index < 0)
            {
                continue;
            }

            var target = plan.Targets[index];
            plan.Targets[index] = new DesktopOrganizationTargetPlan
            {
                SourceBucketId = target.SourceBucketId,
                CategoryId = target.CategoryId,
                TargetWidgetId = target.TargetWidgetId,
                SuggestedDisplayName = customTitle,
                TargetDirectoryPath = Path.Combine(plan.StorageRootPath, customTitle),
                CreatesWidget = true,
                Items = target.Items
            };
        }

        HashSet<string> customBucketIds = _customBoxes
            .Select(b => b.BucketId)
            .ToHashSet(StringComparer.Ordinal);

        var primaryByDestKey = new Dictionary<string, DesktopOrganizationTargetPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in plan.Targets.ToList())
        {
            bool isCustom = customBucketIds.Contains(target.SourceBucketId);
            bool hasVisibleItems = target.Items.Any(i => !_excludedSourcePaths.Contains(i.SourcePath));
            if (!hasVisibleItems && !isCustom)
            {
                continue;
            }

            string destKey = GetTargetEffectiveDestinationKey(target);
            if (primaryByDestKey.TryGetValue(destKey, out var primary))
            {
                foreach (var item in target.Items.ToList())
                {
                    if (primary.Items.All(existing =>
                            !string.Equals(existing.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        primary.Items.Add(item);
                    }

                    _reassignedItemBuckets[item.SourcePath] = primary.SourceBucketId;
                }

                target.Items.Clear();
                _customBoxes.RemoveAll(b => string.Equals(b.BucketId, target.SourceBucketId, StringComparison.Ordinal));
                customBucketIds.Remove(target.SourceBucketId);
                _targetSelections.Remove(target.SourceBucketId);
            }
            else
            {
                primaryByDestKey[destKey] = target;
            }
        }

        plan.Targets.RemoveAll(t => t.Items.Count == 0 && !customBucketIds.Contains(t.SourceBucketId));
    }

    private string GetTargetEffectiveDestinationKey(DesktopOrganizationTargetPlan target)
    {
        if (_targetSelections.TryGetValue(target.SourceBucketId, out var selection))
        {
            if (selection.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget &&
                !string.IsNullOrWhiteSpace(selection.ExistingWidgetId))
            {
                return "existing:" + selection.ExistingWidgetId;
            }

            return "new:" + target.SuggestedDisplayName.Trim();
        }

        if (!target.CreatesWidget && !string.IsNullOrWhiteSpace(target.TargetWidgetId))
        {
            return "existing:" + target.TargetWidgetId;
        }

        return "new:" + target.SuggestedDisplayName.Trim();
    }

    private void RenderPlan(DesktopOrganizationPlan plan, string? renameBucketIdAfterRender = null)
    {
        double offset = PreviewScrollViewer.VerticalOffset;
        ApplyUserBoxCustomizations(plan);
        ReleasePreviewCards();
        UpdateStorageLocationDisplay(plan.StorageRootPath);
        RenderSources(plan);
        var destinations = CreateDestinationOptions();
        DesktopOrganizationPreviewCard? cardToRename = null;

        foreach (var target in plan.Targets)
        {
            if (!_targetSelections.TryGetValue(target.SourceBucketId, out var selection))
            {
                selection = new DesktopOrganizationTargetSelection
                {
                    SourceBucketId = target.SourceBucketId,
                    IsSelected = true,
                    DestinationMode = target.CreatesWidget
                        ? DesktopOrganizationDestinationMode.Dynamic
                        : DesktopOrganizationDestinationMode.ExistingWidget,
                    ExistingWidgetId = target.CreatesWidget ? null : target.TargetWidgetId
                };
                _targetSelections[target.SourceBucketId] = selection;
            }
            if (selection.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget &&
                !destinations.Any(item => item.Id == selection.ExistingWidgetId))
            {
                selection.DestinationMode = target.CreatesWidget
                    ? DesktopOrganizationDestinationMode.Dynamic
                    : DesktopOrganizationDestinationMode.ExistingWidget;
                selection.ExistingWidgetId = target.CreatesWidget ? null : target.TargetWidgetId;
                _excludedSourcePaths.UnionWith(target.Items.Select(item => item.SourcePath));
                ShowSelectionFeedback(T("DesktopOrganization.Layout.DestinationReset"), showPendingLink: false);
            }
            bool isCustomBox = _customBoxes.Any(b =>
                string.Equals(b.BucketId, target.SourceBucketId, StringComparison.Ordinal));
            selection.IsSelected = isCustomBox || target.Items.Any(item => !_excludedSourcePaths.Contains(item.SourcePath));
            var card = new DesktopOrganizationPreviewCard(
                target,
                selection,
                _excludedSourcePaths,
                destinations,
                DesktopOrganizationItemViewMode.CompactGrid)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = selection.IsSelected ? Visibility.Visible : Visibility.Collapsed
            };
            card.SetNewItems(_newPaths);
            card.SelectionChanged += (_, _) =>
            {
                if (!selection.IsSelected && isCustomBox)
                {
                    _customBoxes.RemoveAll(b =>
                        string.Equals(b.BucketId, target.SourceBucketId, StringComparison.Ordinal));
                    _customBoxTitles.Remove(target.SourceBucketId);
                    _targetSelections.Remove(target.SourceBucketId);
                }

                card.Visibility = selection.IsSelected ? Visibility.Visible : Visibility.Collapsed;
                UpdateSummary(_plan);
                UpdateDestinationPaths();
                if (_plan is not null)
                {
                    RenderExcludedItems(_plan);
                }
                UpdateSectionVisibility();
            };
            card.DestinationRouteChanged += (_, _) =>
            {
                if (_plan is not null)
                {
                    RenderPlan(_plan);
                }
            };
            card.FilesDroppedIntoCard += (_, sourcePaths) => MoveItemsToBox(sourcePaths, target.SourceBucketId);
            card.BoxTitleRenamed += (_, newTitle) => RenameBox(target.SourceBucketId, newTitle);
            if (!string.IsNullOrWhiteSpace(renameBucketIdAfterRender) &&
                string.Equals(target.SourceBucketId, renameBucketIdAfterRender, StringComparison.Ordinal))
            {
                cardToRename = card;
            }

            _targetCards.Add(card);
            TargetRows.Children.Add(card);
        }

        if (!_hasCompletedExecution && HasAnyActiveScopeItems(plan))
        {
            _newBoxGhostCard = CreateNewBoxDropCard();
            _newBoxGhostCard.Visibility = Visibility.Visible;
            TargetRows.Children.Add(_newBoxGhostCard);
            DesktopOrganizationPreviewCard.DragSessionStateChanged += OnDragSessionStateChanged;
            DesktopOrganizationPreviewCard.PointerDragMoved += OnPointerDragMoved;
            DesktopOrganizationPreviewCard.PointerDragReleased += OnPointerDragReleased;
        }

        UpdateSummary(plan);
        RenderExcludedItems(plan);
        UpdateSectionVisibility();
        UpdateDestinationPaths();
        _isPreviewReady = true;
        UpdateSummary(plan);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                PreviewScrollViewer.ChangeView(null, offset, null, disableAnimation: true);
                cardToRename?.BeginInlineRename();
            }
        });
    }

    private FrameworkElement CreateNewBoxDropCard()
    {
        AppSettings appSettings = App.Current.SettingsService.Settings;
        double cornerRadius = WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
            WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                appSettings.WidgetCornerPreference));
        Brush secondaryBrush = NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0x9E, 0x9E, 0x9E));
        Brush defaultStroke = NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x9E, 0x9E, 0x9E));
        Brush accentStroke = NeutralInteractionBrush.ResolveThemedResource("AccentFillColorDefaultBrush", this)
            ?? defaultStroke;

        var dashedOutline = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            RadiusX = cornerRadius,
            RadiusY = cornerRadius,
            Stroke = defaultStroke,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
            Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsHitTestVisible = false
        };

        var plusIcon = new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = secondaryBrush
        };
        var labelText = new TextBlock
        {
            Text = T("DesktopOrganization.Preview.DynamicDestination"),
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = secondaryBrush
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };
        stack.Children.Add(plusIcon);
        stack.Children.Add(labelText);

        Brush hoverFill = NeutralInteractionBrush.ResolveThemedResource("SubtleFillColorSecondaryBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        Brush transparentFill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var dropCard = new Grid
        {
            Width = DesktopOrganizationPreviewCard.FixedBoxWidth,
            Height = DesktopOrganizationPreviewCard.FixedBoxHeight,
            Background = transparentFill,
            AllowDrop = true
        };
        dropCard.Children.Add(dashedOutline);
        dropCard.Children.Add(stack);

        _setGhostBoxHighlight = highlighted =>
        {
            dashedOutline.Stroke = highlighted ? accentStroke : defaultStroke;
            dashedOutline.Fill = highlighted ? hoverFill : transparentFill;
            plusIcon.Foreground = highlighted ? accentStroke : secondaryBrush;
            labelText.Foreground = highlighted ? accentStroke : secondaryBrush;
        };

        dropCard.PointerEntered += (_, _) =>
        {
            if (DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths.Count == 0 &&
                string.IsNullOrWhiteSpace(DesktopOrganizationPreviewCard.ActiveDraggedSourcePath))
            {
                dashedOutline.Fill = hoverFill;
                plusIcon.Foreground = NeutralInteractionBrush.ResolveThemedResource("TextFillColorPrimaryBrush", this) ?? secondaryBrush;
                labelText.Foreground = NeutralInteractionBrush.ResolveThemedResource("TextFillColorPrimaryBrush", this) ?? secondaryBrush;
            }
        };
        dropCard.PointerExited += (_, _) =>
        {
            if (DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths.Count == 0 &&
                string.IsNullOrWhiteSpace(DesktopOrganizationPreviewCard.ActiveDraggedSourcePath))
            {
                dashedOutline.Fill = transparentFill;
                plusIcon.Foreground = secondaryBrush;
                labelText.Foreground = secondaryBrush;
            }
        };
        dropCard.Tapped += (_, _) => CreateEmptyNewBox();

        dropCard.DragOver += (_, e) =>
        {
            if (DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            if (_isExecuting || _isScanning || _hasCompletedExecution ||
                (DesktopOrganizationPreviewCard.ActiveDraggedSourcePaths.Count == 0 &&
                 string.IsNullOrWhiteSpace(DesktopOrganizationPreviewCard.ActiveDraggedSourcePath)))
            {
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = T("DesktopOrganization.Layout.RouteBadgeNew");
            e.DragUIOverride.IsCaptionVisible = true;
            dashedOutline.Stroke = accentStroke;
            plusIcon.Foreground = accentStroke;
            labelText.Foreground = accentStroke;
        };
        dropCard.DragLeave += (_, _) =>
        {
            dashedOutline.Stroke = defaultStroke;
            plusIcon.Foreground = secondaryBrush;
            labelText.Foreground = secondaryBrush;
        };
        dropCard.Drop += async (_, e) =>
        {
            dashedOutline.Stroke = defaultStroke;
            plusIcon.Foreground = secondaryBrush;
            labelText.Foreground = secondaryBrush;

            if (_isExecuting || _isScanning || _hasCompletedExecution ||
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

            if (droppedPaths.Count > 0)
            {
                CreateNewBoxWithFiles(droppedPaths);
            }
        };

        return dropCard;
    }

    private void MoveItemsToBox(IReadOnlyList<string> sourcePaths, string targetBucketId)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || _basePlan is null || _plan is null ||
            sourcePaths.Count == 0)
        {
            return;
        }

        foreach (string sourcePath in sourcePaths)
        {
            EnsureSourcePathIncludedInPlan(sourcePath);
            _excludedSourcePaths.Remove(sourcePath);
            _reassignedItemBuckets[sourcePath] = targetBucketId;
        }

        DesktopOrganizationPreviewCard.ClearActiveTileSelection();
        if (_targetSelections.TryGetValue(targetBucketId, out var selection))
        {
            selection.IsSelected = true;
        }

        RenderPlan(_plan);
    }

    private void CreateEmptyNewBox()
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || _plan is null)
        {
            return;
        }

        string bucketId = $"custom-box-{++_customBoxCounter}";
        string defaultTitle = $"{T("DesktopOrganization.Preview.DynamicDestination")} {_customBoxCounter}";
        _customBoxes.Add((bucketId, defaultTitle));
        DesktopOrganizationPreviewCard.ClearActiveTileSelection();
        _targetSelections[bucketId] = new DesktopOrganizationTargetSelection
        {
            SourceBucketId = bucketId,
            IsSelected = true,
            DestinationMode = DesktopOrganizationDestinationMode.Dynamic
        };

        RenderPlan(_plan, renameBucketIdAfterRender: bucketId);
    }

    private void CreateNewBoxWithFiles(IReadOnlyList<string> sourcePaths)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || _basePlan is null || _plan is null ||
            sourcePaths.Count == 0)
        {
            return;
        }

        string bucketId = $"custom-box-{++_customBoxCounter}";
        string defaultTitle = $"{T("DesktopOrganization.Preview.DynamicDestination")} {_customBoxCounter}";
        _customBoxes.Add((bucketId, defaultTitle));

        foreach (string sourcePath in sourcePaths)
        {
            EnsureSourcePathIncludedInPlan(sourcePath);
            _excludedSourcePaths.Remove(sourcePath);
            _reassignedItemBuckets[sourcePath] = bucketId;
        }

        DesktopOrganizationPreviewCard.ClearActiveTileSelection();
        _targetSelections[bucketId] = new DesktopOrganizationTargetSelection
        {
            SourceBucketId = bucketId,
            IsSelected = true,
            DestinationMode = DesktopOrganizationDestinationMode.Dynamic
        };

        RenderPlan(_plan, renameBucketIdAfterRender: bucketId);
    }

    private void RenameBox(string bucketId, string newTitle)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || _plan is null ||
            string.IsNullOrWhiteSpace(newTitle))
        {
            return;
        }

        _customBoxTitles[bucketId] = newTitle.Trim();
        if (_targetSelections.TryGetValue(bucketId, out var selection))
        {
            selection.DestinationMode = DesktopOrganizationDestinationMode.Dynamic;
            selection.ExistingWidgetId = null;
        }

        RenderPlan(_plan);
    }

    private void EnsureSourcePathIncludedInPlan(string sourcePath)
    {
        if (_basePlan is null || _plan is null)
        {
            return;
        }

        var snapshot = _plan.SourceItems.FirstOrDefault(i =>
            string.Equals(i.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
        if (snapshot is { CanOptIn: true } && _optionalIncludedPaths.Add(sourcePath))
        {
            _plan = Coordinator.CreatePreviewPlanWithOptionalItems(
                _basePlan,
                _optionalIncludedPaths,
                _plan.IncludePersonalDesktop,
                _plan.IncludePublicDesktop);
        }
    }

    private IReadOnlyList<DesktopOrganizationDestinationOption> CreateDestinationOptions()
    {
        try { return Coordinator.GetDestinationOptions(); }
        catch { return []; }
    }

    private IReadOnlyList<DesktopOrganizationFileSnapshot> GetRetainedPreviewItems()
    {
        if (_hasCompletedExecution && _lastExecutionPlan is not null)
            return _lastExecutionPlan.ExcludedItems.Where(item =>
                DesktopOrganizationPreviewSummary.IncludesSource(_lastExecutionPlan, item)).ToList();
        if (_plan is null) return [];
        var sources = _plan.SourceItems.Count > 0 ? _plan.SourceItems
            : _plan.Targets.SelectMany(target => target.Items).Concat(_plan.ExcludedItems).ToList();
        var selected = _plan.Targets.Where(target =>
                !_targetSelections.TryGetValue(target.SourceBucketId, out var selection) || selection.IsSelected)
            .SelectMany(target => target.Items)
            .Where(item => !_excludedSourcePaths.Contains(item.SourcePath))
            .Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sources.Where(item => DesktopOrganizationPreviewSummary.IncludesSource(_plan, item) &&
            !selected.Contains(item.SourcePath)).Select(item =>
        {
            return item.IsEligible || _optionalIncludedPaths.Contains(item.SourcePath)
                ? item with { ExclusionReason = DesktopOrganizationExclusionReason.UserChoice } : item;
        }).DistinctBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void PreviewSections_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_plan is null) return;
        SelectSection(sender.SelectedItem == ExcludedItemsButton);
    }

    private void SelectSection(bool retained)
    {
        UpdateSectionVisibility();
        UpdateSummary(_plan);
    }

    private void UpdateSectionVisibility()
    {
        bool hasDesktopItems = HasAnyActiveScopeItems(_plan);
        TargetSelectionHost.Visibility = !_hasCompletedExecution && hasDesktopItems
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetainedItemsPanel.Visibility = Visibility.Collapsed;
        CompletedViewPanel.Visibility = _hasCompletedExecution
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyPreviewCard.Visibility = !_hasCompletedExecution && !hasDesktopItems
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyPreviewText.Text = T(_plan is { IncludePersonalDesktop: false, IncludePublicDesktop: false }
            ? "DesktopOrganization.Layout.NoSource"
            : "DesktopOrganization.Preview.NothingAction");
        StayOnDesktopTrayBorder.Visibility = !_hasCompletedExecution && hasDesktopItems
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewSections.SelectedItem = SummaryTitle;
        ExecuteButton.Visibility = !_hasCompletedExecution ? Visibility.Visible : Visibility.Collapsed;
        ResetSelectionMenuItem.IsEnabled = !_hasCompletedExecution && !_isExecuting && !_isScanning;
    }

    private void UpdateSummary(DesktopOrganizationPlan? plan)
    {
        if (plan is null)
        {
            PageHeaderTitleText.Text = T("DesktopOrganization.Window.Title");
            FooterSummaryText.Text = string.Empty;
            FooterSafetyHintText.Visibility = Visibility.Collapsed;
            return;
        }

        var summary = DesktopOrganizationPreviewSummary.Calculate(plan, _targetSelections, _excludedSourcePaths);
        int count = _hasCompletedExecution ? _completedItems.Count : summary.SelectedCount;
        int retainedCount = _hasCompletedExecution
            ? GetRetainedPreviewItems().Count + _runtimeRetainedItems.Count
            : summary.RetainedCount;

        PageHeaderTitleText.Text = T(_hasCompletedExecution
            ? "DesktopOrganization.Result.SuccessTitle"
            : "DesktopOrganization.Window.Title");
        SummaryTitle.Text = Format(
            _hasCompletedExecution ? "DesktopOrganization.Layout.CompletedCount" : "DesktopOrganization.Layout.MoveCount",
            count);
        SummaryDescription.Text = Format("DesktopOrganization.Layout.TotalCount", summary.TotalCount);
        ExcludedItemsButton.Text = Format("DesktopOrganization.Layout.RetainedCount", Math.Max(0, retainedCount));

        if (_hasCompletedExecution)
        {
            FooterSummaryText.Text = T("DesktopOrganization.Preview.Completed");
            FooterSafetyHintText.Visibility = Visibility.Collapsed;
        }
        else if (!plan.IncludePersonalDesktop && !plan.IncludePublicDesktop)
        {
            FooterSummaryText.Text = T("DesktopOrganization.Layout.NoSource");
            FooterSafetyHintText.Visibility = Visibility.Collapsed;
        }
        else if (plan.EligibleItemCount == 0 && summary.SelectedCount == 0)
        {
            FooterSummaryText.Text = T("DesktopOrganization.Preview.NothingAction");
            FooterSafetyHintText.Visibility = Visibility.Collapsed;
        }
        else if (summary.SelectedCount == 0)
        {
            FooterSummaryText.Text = T("DesktopOrganization.Layout.NoSelection");
            FooterSafetyHintText.Visibility = Visibility.Collapsed;
        }
        else
        {
            string sizeText = DesktopOrganizationPreviewSummary.FormatBytes(
                summary.SelectedBytes,
                App.Current.LocalizationService);
            FooterSummaryText.Text = $"{Format("DesktopOrganization.Layout.MoveCount", summary.SelectedCount)} · {sizeText}";
            FooterSafetyHintText.Text = "· " + Format(
                "DesktopOrganization.Layout.TargetSummary",
                summary.DestinationCount,
                summary.NewDestinationCount);
            FooterSafetyHintText.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(FooterSummaryText, T("DesktopOrganization.Layout.HeaderSubtitle"));
        }

        ExecuteButton.Content = Format("DesktopOrganization.Layout.Execute", summary.SelectedCount);
        bool pendingUndo = App.Current.SettingsService.OrganizationHistory.Entries.Any(entry =>
            entry.ActionType == OrganizationActionType.DesktopOrganization && entry.UndoStarted && entry.CanUndo);
        bool overlapsDesktop = !string.IsNullOrWhiteSpace(plan.StorageRootPath) &&
            ManagedStoragePathService.OverlapsDesktop(plan.StorageRootPath);
        ExecuteButton.IsEnabled = _isPreviewReady &&
            summary.SelectedCount > 0 &&
            !_isScanning &&
            !_isExecuting &&
            !_hasCompletedExecution &&
            !pendingUndo &&
            !HasPendingRecovery &&
            !overlapsDesktop;
    }

    private void RenderExecutionResult(IEnumerable<OrganizationHistoryItem> completedItems)
    {
        foreach (var item in completedItems)
        {
            if (item.IsRestored)
            {
                _completedItems.Remove(item.SourcePath);
                _restoredItems[item.SourcePath] = item;
            }
            else
            {
                _completedItems[item.SourcePath] = item;
                _restoredItems.Remove(item.SourcePath);
            }
        }
        ReleaseCompletedCards();
        foreach (var group in _completedItems.Values.GroupBy(item => item.TargetWidgetId))
        {
            var items = group.Select(item => FindPreviewSnapshot(item.SourcePath, item.Name, item.SourceScope) with
            { SourcePath = item.DestinationPath, Name = Path.GetFileName(item.DestinationPath) }).ToList();
            var card = new DesktopOrganizationPreviewCard(
                group.Key,
                group.First().TargetWidgetName,
                string.Empty,
                items,
                [],
                false,
                item => item.SourcePath,
                DesktopOrganizationItemViewMode.CompactGrid)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            _completedCards.Add(card);
            CompletedItemsPanel.Children.Add(card);
        }

        string storageRoot = (_lastExecutionPlan ?? _plan)?.StorageRootPath ?? StoragePathText.Text;
        int untouchedCount = GetRetainedPreviewItems().Count + _runtimeRetainedItems.Count;
        CompletedHeroTitleText.Text = Format("DesktopOrganization.Layout.CompletedCount", _completedItems.Count);
        CompletedHeroBodyText.Text = Format(
            "DesktopOrganization.Layout.CompletedCardBody",
            storageRoot,
            untouchedCount);
        CompletedUndoButton.Visibility = !string.IsNullOrWhiteSpace(_lastHistoryId)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ReleasePreviewCards();
        RenderExcludedItems(_lastExecutionPlan ?? _plan!);
        UpdateSummary(_plan);
        UpdateSectionVisibility();
        UpdateDestinationPaths();
    }

    private void ReleaseCompletedCards()
    {
        foreach (var card in _completedCards) card.Dispose();
        _completedCards.Clear();
        CompletedItemsPanel.Children.Clear();
    }

    private void ShowSelectionFeedback(string text, bool showPendingLink = true)
    {
        SelectionFeedbackInfo.Message = text;
        ViewPendingButton.Visibility = showPendingLink ? Visibility.Visible : Visibility.Collapsed;
        SelectionFeedbackInfo.IsOpen = true;
        Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(SelectionFeedbackInfo)?
            .RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void ViewPendingButton_Click(object sender, RoutedEventArgs e)
    {
        SelectSection(false);
        SelectionFeedbackInfo.IsOpen = false;
    }

    private void UpdateDestinationPaths()
    {
        DestinationPathsPanel.Children.Clear();
        if (_plan is null) return;
        var destinations = CreateDestinationOptions();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in (_hasCompletedExecution ? _lastExecutionPlan : _plan)?.Targets ?? [])
        {
            _targetSelections.TryGetValue(target.SourceBucketId, out var selection);
            var destination = !_hasCompletedExecution && selection?.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget
                ? destinations.FirstOrDefault(item => item.Id == selection.ExistingWidgetId) : null;
            string path = destination?.DirectoryPath ?? target.TargetDirectoryPath;
            if (!seen.Add(path)) continue;
            DestinationPathsPanel.Children.Add(new TextBlock
            {
                Text = (destination?.DisplayName ?? target.SuggestedDisplayName) + "\n" + path,
                Style = (Style)Resources["DesktopOrganizationSecondaryTextStyle"],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
        }
    }
}
